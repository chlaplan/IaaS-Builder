using System.Net.Http.Headers;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Azure;

/// <summary>
/// <see cref="IAzureDeploymentService"/> implemented with the Azure SDK for .NET.
/// </summary>
/// <remarks>
/// Replaces the Az PowerShell module dependency. Beyond being async and cancellable, this
/// removes the legacy script's ~40 lines of module bootstrapping, version pinning and
/// self-updating at startup, which was both slow and (because of the broken string-based
/// version comparisons) frequently wrong.
/// </remarks>
public sealed class AzureDeploymentService : IAzureDeploymentService
{
    // Long enough to outlast a full build - SCCM and Exchange roles can configure for well over an
    // hour, and a session host that boots late still has to fetch the package. Short enough that a
    // token leaked in deployment history is not useful for long.
    private const int SasLifetimeHours = 12;
    private const int SasClockSkewMinutes = 15;

    private readonly TokenCredential _credential;
    private readonly TemplateResolver _templates;
    private readonly ArmClient _client;

    public AzureDeploymentService(TokenCredential credential, AzureCloud cloud, TemplateResolver templates)
        : this(credential, cloud, templates, transport: null)
    {
    }

    /// <summary>
    /// Allows tests to answer ARM from a stub instead of the network. The deployment path is the
    /// one part of this tool that cannot be exercised without a subscription, and it is also the
    /// part an SDK upgrade is most likely to break silently, so it is worth the seam.
    /// </summary>
    internal AzureDeploymentService(
        TokenCredential credential,
        AzureCloud cloud,
        TemplateResolver templates,
        HttpPipelineTransport? transport)
    {
        _credential = credential;
        _templates = templates;

        var options = new ArmClientOptions
        {
            Environment = AzureCloudEndpoints.GetArmEnvironment(cloud)
        };

        if (transport is not null)
        {
            options.Transport = transport;
            // Otherwise a stubbed failure response would be retried with real backoff and the
            // test would take half a minute to tell us something it already knows.
            options.Retry.MaxRetries = 0;
        }

        _client = new ArmClient(credential, default, options);
        Cloud = cloud;
    }

    public AzureCloud Cloud { get; }

    private SubscriptionResource GetSubscription(DeploymentPlan plan) =>
        _client.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{plan.Azure.SubscriptionId}"));

    public async Task EnsureResourceGroupAsync(DeploymentPlan plan, CancellationToken ct)
    {
        var groups = GetSubscription(plan).GetResourceGroups();

        // Scoped existence check. The legacy script listed every resource group in the
        // subscription and compared the resulting array to a string, which was only ever
        // true when the subscription held exactly one group.
        if (await groups.ExistsAsync(plan.Azure.ResourceGroup, ct))
        {
            return;
        }

        var data = new ResourceGroupData(new AzureLocation(plan.Azure.Location));
        foreach (var (key, value) in plan.Azure.Tags)
        {
            data.Tags[key] = value;
        }

        await groups.CreateOrUpdateAsync(WaitUntil.Completed, plan.Azure.ResourceGroup, data, ct);
    }

    public async Task<ArtifactLocation> PublishArtifactsAsync(DeploymentPlan plan, CancellationToken ct)
    {
        if (plan.Artifacts.SkipUpload)
        {
            var preStaged = plan.Artifacts.ArtifactsLocationOverride
                   ?? throw new InvalidOperationException("Artifact upload is disabled but no location was supplied.");

            return new ArtifactLocation(preStaged, NormalizeSasToken(plan.Artifacts.ArtifactsSasTokenOverride));
        }

        // Nothing to stage: the DSC extension fetches the package straight from its published URL,
        // so there is no storage account, no blob data role and no SAS token involved at all.
        if (plan.Artifacts.UsePublicPackageUrl)
        {
            if (!PublicPackageSource.TrySplit(plan.Artifacts.PublicPackageUrl, out var baseUri, out var fileName))
            {
                throw new InvalidOperationException(
                    $"'{plan.Artifacts.PublicPackageUrl}' is not a direct HTTPS link to the DSC "
                    + "package. It must end in the file itself, for example "
                    + $"{PublicPackageSource.DefaultUrl}");
            }

            await EnsurePackageReachableAsync(plan.Artifacts.PublicPackageUrl, ct);
            return new ArtifactLocation(baseUri, "", fileName);
        }

        // An operator who cannot get a blob data role granted on demand can nominate a long-lived
        // account they already have rights on, instead of the fresh random-named one created in
        // the lab's resource group - which is why a grant never survived to the next run.
        var stagingGroupName = StagingResourceGroup(plan);
        var nominated = !string.IsNullOrWhiteSpace(plan.Artifacts.StorageResourceGroup);

        var resourceGroup = await GetSubscription(plan)
            .GetResourceGroups()
            .GetAsync(stagingGroupName, ct);

        var accounts = resourceGroup.Value.GetStorageAccounts();
        StorageAccountResource account;

        if (await accounts.ExistsAsync(plan.Artifacts.StorageAccountName, cancellationToken: ct))
        {
            account = await accounts.GetAsync(plan.Artifacts.StorageAccountName, cancellationToken: ct);
        }
        else if (nominated)
        {
            // Creating it here would put a new account into a resource group the operator shares,
            // under a name they only meant to reference. A typo is the likeliest cause.
            throw new InvalidOperationException(
                $"Storage account '{plan.Artifacts.StorageAccountName}' was not found in resource "
                + $"group '{stagingGroupName}'. Nothing was created.\n\n"
                + "That resource group is nominated on the Storage page, which means the account is "
                + "expected to exist already so the blob data role granted on it can be reused "
                + "across runs. Check the account and resource group names, or clear the resource "
                + "group to have the lab create its own staging account instead.");
        }
        else
        {
            var sku = new StorageSku(new StorageSkuName(plan.Artifacts.StorageSku));
            var content = new StorageAccountCreateOrUpdateContent(
                sku, StorageKind.StorageV2, new AzureLocation(plan.Azure.Location))
            {
                AllowBlobPublicAccess = false,
                MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
                EnableHttpsTrafficOnly = true
            };

            var operation = await accounts.CreateOrUpdateAsync(
                WaitUntil.Completed, plan.Artifacts.StorageAccountName, content, ct);
            account = operation.Value;
        }

        var blobEndpoint = account.Data.PrimaryEndpoints?.BlobUri
                           ?? new Uri($"https://{plan.Artifacts.StorageAccountName}.{AzureCloudEndpoints.GetBlobSuffix(Cloud)}/");

        var containerUri = new Uri(blobEndpoint, plan.Artifacts.ContainerName);
        var containerClient = new BlobContainerClient(containerUri, _credential);

        // Entra first, account key as a fallback. Creating the container is the first data-plane
        // call, so it doubles as the probe: if it is refused, the signed-in account has no blob
        // data role and every later call would be refused the same way.
        //
        // Owner and Contributor grant no data-plane access at all, so this is the common case
        // rather than an edge case - but both CAN read the staging account's keys, and this tool
        // created that account seconds earlier. Failing here asked the operator for a role
        // assignment they did not actually need.
        StorageSharedKeyCredential? sharedKey = null;

        // Probed at most once and remembered, so the container failure, the upload failure and the
        // SAS failure all explain the same underlying cause rather than each guessing separately.
        SharedKeyStatus? keyStatus = null;
        string? keyDetail = null;
        async ValueTask<SharedKeyStatus> KeyStatusAsync()
        {
            if (keyStatus is null)
            {
                var (credential, status, detail) = await TryGetSharedKeyWithReasonAsync(account, ct);
                sharedKey ??= credential;
                keyStatus = status;
                keyDetail = detail;
            }

            return keyStatus.Value;
        }

        try
        {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 403)
        {
            var status = await KeyStatusAsync();
            if (sharedKey is null)
            {
                throw new InvalidOperationException(
                    DataPlaneAccessMessage(plan, $"create the '{plan.Artifacts.ContainerName}' container", status, keyDetail), ex);
            }

            containerClient = new BlobContainerClient(containerUri, sharedKey);
            try
            {
                await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);
            }
            catch (RequestFailedException inner) when (inner.Status is 403)
            {
                throw new InvalidOperationException(
                    DataPlaneAccessMessage(plan, $"create the '{plan.Artifacts.ContainerName}' container", status, keyDetail), inner);
            }
        }

        var packagePath = _templates.Resolve(plan.Artifacts.DscPackagePath);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException($"DSC package not found: {packagePath}", packagePath);
        }

        var blobName = Path.GetFileName(packagePath);
        var blobClient = containerClient.GetBlobClient(blobName);

        // Awaited, not fired into a background job followed by a fixed 60 second sleep.
        try
        {
            await using var stream = File.OpenRead(packagePath);
            await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 403)
        {
            throw new InvalidOperationException(
                DataPlaneAccessMessage(plan, "upload the DSC package", await KeyStatusAsync(), keyDetail), ex);
        }

        var sasToken = await CreateReadSasAsync(blobEndpoint, plan, containerClient.Name, sharedKey, ct);
        return new ArtifactLocation(blobEndpoint.ToString(), sasToken);
    }

    /// <summary>
    /// Why the shared-key fallback could not be used.
    /// </summary>
    /// <remarks>
    /// The caller knew which of these happened and used to discard it, leaving the operator a
    /// message that listed both causes and both fixes and left them to work out which applied.
    /// They are not the same problem: one is a missing role assignment, the other is a tenant
    /// policy that also invalidates the read SAS the DSC extension needs - so "grant yourself the
    /// blob data role" would not have fixed it on its own.
    /// </remarks>
    internal enum SharedKeyStatus
    {
        Available,
        DisabledOnAccount,
        CannotListKeys
    }

    /// <summary>
    /// The staging account's access key, or null with the reason it cannot be used.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing for both of the ways this legitimately fails: a hardened
    /// tenant that sets <c>allowSharedKeyAccess = false</c>, and a principal without the
    /// control-plane listKeys action. In both cases the caller's original data-plane error is the
    /// one worth reporting, so this must not replace it.
    /// </remarks>
    private static async Task<(StorageSharedKeyCredential? Credential, SharedKeyStatus Status, string? Detail)>
        TryGetSharedKeyWithReasonAsync(StorageAccountResource account, CancellationToken ct)
    {
        if (account.Data.AllowSharedKeyAccess is false)
        {
            return (null, SharedKeyStatus.DisabledOnAccount, null);
        }

        try
        {
            var keys = await account.GetKeysAsync(cancellationToken: ct);
            foreach (var key in keys.Value.Keys)
            {
                if (!string.IsNullOrEmpty(key.Value))
                {
                    return (new StorageSharedKeyCredential(account.Data.Name, key.Value),
                        SharedKeyStatus.Available, null);
                }
            }
        }
        catch (RequestFailedException ex)
        {
            // Contributor and Owner both have listKeys, so reaching here is unexpected and the
            // reason is the only thing that makes the next attempt cheaper. Discarding it left the
            // operator with advice that may not even address what went wrong.
            return (null, SharedKeyStatus.CannotListKeys, DescribeKeyFailure(ex));
        }

        return (null, SharedKeyStatus.CannotListKeys,
            "listKeys succeeded but returned no usable key.");
    }

    /// <summary>
    /// One line naming the status and error code, so the log says which wall was hit.
    /// </summary>
    internal static string DescribeKeyFailure(RequestFailedException ex)
    {
        var code = string.IsNullOrWhiteSpace(ex.ErrorCode) ? "no error code" : ex.ErrorCode;
        var first = (ex.Message ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no message";

        return $"listKeys failed with HTTP {ex.Status} ({code}): {first}";
    }

    /// <summary>
    /// The advice that actually fits <paramref name="status"/>, rather than both possibilities at
    /// once.
    /// </summary>
    internal static string ExplainUploadFailure(
        SharedKeyStatus status,
        string accountName,
        string resourceGroup,
        string subscriptionId,
        string? detail = null)
    {
        var grant =
            $"az role assignment create --assignee <your-upn-or-object-id> " +
            $"--role \"Storage Blob Data Contributor\" " +
            $"--scope /subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}";

        // Only ever appended, never substituted for the advice: the reason explains what was
        // tried, the advice is still what the operator has to do next.
        var because = string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : $"\n\nUnderlying reason: {detail}";

        return status switch
        {
            // The hardened-tenant case. Granting the blob data role fixes the upload but NOT the
            // download: with shared key off, service and account SAS are refused outright, so the
            // read SAS has to be a user delegation SAS, and generating one needs a second role
            // that Storage Blob Data Contributor does not include.
            SharedKeyStatus.DisabledOnAccount =>
                $"Storage account '{accountName}' has shared key access disabled, which is usually "
                + "the Azure Policy 'Storage accounts should prevent shared key access'. That "
                + "blocks both the upload and the read SAS the DSC extension needs, so the account "
                + "key fallback cannot help here.\n\n"
                + "Grant the signed-in account BOTH of these and retry:\n"
                + $"  {grant}\n"
                + $"  {grant.Replace("Storage Blob Data Contributor", "Storage Blob Delegator")}\n\n"
                + "'Storage Blob Delegator' is the separate role that allows generating the user "
                + "delegation SAS, and it is not included in 'Storage Blob Data Contributor'. Role "
                + "assignments can take a few minutes to take effect.\n\n"
                + "If policy also prevents you holding those roles, use the offline path instead: "
                + "set artifacts.skipUpload with artifactsLocationOverride and "
                + "artifactsSasTokenOverride to point at a pre-staged copy of the package.",

            // The ordinary case: nothing is disabled, the account simply has no data-plane grant
            // and the sign-in cannot read the keys either.
            _ =>
                $"The signed-in account cannot write blob data in storage account '{accountName}', "
                + "and could not read the account keys to fall back on. Subscription Owner and "
                + "Contributor are control-plane only and grant no access to blob data.\n\n"
                + "Grant the signed-in account this role and retry:\n"
                + $"  {grant}\n\n"
                + "Role assignments can take a few minutes to take effect. Scope it to the "
                + "subscription instead if you build labs in new resource groups often, because "
                + "the staging account gets a fresh name on every run.\n\n"
                + "In a pre-staged or air-gapped environment, set artifacts.skipUpload with "
                + "artifactsLocationOverride and artifactsSasTokenOverride instead."
                + because
        };
    }

    /// <summary>
    /// Read-only, container-scoped, time-limited SAS for the DSC extension to fetch the package.
    /// </summary>
    /// <remarks>
    /// Signed with Entra credentials (a user delegation SAS) whenever the upload itself used them,
    /// because that SAS is bound to the signing identity and is independently revocable. When the
    /// upload had to fall back to the account key the delegation key is unavailable too - asking
    /// for one needs the same data-plane role that was just refused - so the SAS is signed with the
    /// same key. Either way it is read+list only, container-scoped and expires in hours.
    /// </remarks>
    private async Task<string> CreateReadSasAsync(
        Uri blobEndpoint,
        DeploymentPlan plan,
        string containerName,
        StorageSharedKeyCredential? sharedKey,
        CancellationToken ct)
    {
        // Backdated to absorb clock skew between this machine and the storage service; a SAS that
        // starts "now" is intermittently rejected as not yet valid.
        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-SasClockSkewMinutes);
        var expiresOn = DateTimeOffset.UtcNow.AddHours(SasLifetimeHours);

        var sas = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            Resource = "c",
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
            Protocol = SasProtocol.Https
        };

        sas.SetPermissions(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List);

        if (sharedKey is not null)
        {
            return NormalizeSasToken(sas.ToSasQueryParameters(sharedKey).ToString());
        }

        var serviceClient = new BlobServiceClient(blobEndpoint, _credential);

        UserDelegationKey delegationKey;
        try
        {
            var response = await serviceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, ct);
            delegationKey = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status is 403)
        {
            // Distinct from the upload failure: the upload succeeded over Entra, so blob data
            // access is fine. Generating a user delegation SAS needs a *different* action,
            // generateUserDelegationKey, whose least-privileged role is 'Storage Blob Delegator' -
            // and that is not included in 'Storage Blob Data Contributor'. Recommending the blob
            // data role here would send the operator to re-grant something they already have.
            throw new InvalidOperationException(
                $"The DSC package uploaded to '{plan.Artifacts.StorageAccountName}', but signing a "
                + "read SAS for the VMs to fetch it was denied.\n\n"
                + "Generating a user delegation SAS needs the 'Storage Blob Delegator' role, which "
                + "is separate from 'Storage Blob Data Contributor'. Grant it and retry:\n"
                + $"  az role assignment create --assignee <your-upn-or-object-id> "
                + $"--role \"Storage Blob Delegator\" "
                + $"--scope /subscriptions/{plan.Azure.SubscriptionId}/resourceGroups/{plan.Azure.ResourceGroup}\n\n"
                + "Alternatively, allow shared key access on the staging account, which lets the "
                + "tool sign the SAS with the account key instead.",
                ex);
        }

        var token = sas
            .ToSasQueryParameters(delegationKey, plan.Artifacts.StorageAccountName)
            .ToString();

        return NormalizeSasToken(token);
    }

    /// <summary>
    /// The templates concatenate the token straight onto the blob path, so it has to carry its own
    /// '?'. Callers supplying an override should not have to know that.
    /// </summary>
    private static string NormalizeSasToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "";
        }

        var trimmed = token.Trim();
        return trimmed.StartsWith('?') ? trimmed : "?" + trimmed;
    }

    /// <summary>
    /// Reached only when Entra blob access and the account-key fallback have <em>both</em> failed.
    /// The advice depends entirely on <em>why</em> the key fallback was unavailable, so the reason
    /// is threaded in rather than both possibilities being listed for the operator to triage.
    /// </summary>
    /// <summary>
    /// The resource group the staging account lives in: the operator's nominated one when set,
    /// otherwise the lab's own.
    /// </summary>
    /// <remarks>
    /// The role-grant advice has to be scoped to this and not to the lab group, or it would tell
    /// the operator to grant access on a resource group the account is not even in.
    /// </remarks>
    internal static string StagingResourceGroup(DeploymentPlan plan) =>
        string.IsNullOrWhiteSpace(plan.Artifacts.StorageResourceGroup)
            ? plan.Azure.ResourceGroup
            : plan.Artifacts.StorageResourceGroup.Trim();

    private static readonly HttpClient PackageProbe = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Confirms the published package is actually downloadable before any VM is built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DSC extension runs at the very end of a deployment, so a wrong URL surfaces as a failed
    /// extension twenty minutes and several VMs later. A typo, a renamed branch or a repository
    /// turned private are all cheap to detect here and expensive to detect there.
    /// </para>
    /// <para>
    /// This proves the URL is reachable <em>from this machine</em>, which is not the same as being
    /// reachable from inside the VM - the message says so rather than implying a guarantee it
    /// cannot make. A staged blob needed the same outbound access, so this is not a new
    /// requirement, only a newly visible one.
    /// </para>
    /// </remarks>
    private static async Task EnsurePackageReachableAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            response = await PackageProbe.SendAsync(head, ct);

            // Some hosts refuse HEAD but serve GET perfectly well, so a refusal is not an answer.
            if (response.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed
                or System.Net.HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                response = await PackageProbe.SendAsync(
                    get, HttpCompletionOption.ResponseHeadersRead, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(UnreachablePackageMessage(url, ex.Message), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    UnreachablePackageMessage(url, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
            }
        }
    }

    internal static string UnreachablePackageMessage(string url, string reason) =>
        $"The DSC package at '{url}' could not be downloaded: {reason}\n\n"
        + "Nothing was created. The DSC extension fetches this URL from inside each VM, so a "
        + "package that cannot be downloaded would fail every VM after they had all been built.\n\n"
        + "This was checked from this machine, not from inside the VM. If the URL is correct but "
        + "your network blocks it, the VMs are likely to be blocked too.\n\n"
        + "Fix whichever applies:\n"
        + "  - Correct the package URL on the Storage page. It must link directly to the file, not "
        + "to a web page about it.\n"
        + "  - Host the package somewhere your environment can reach and point the URL at that.\n"
        + "  - Turn off 'Fetch the DSC package from a public URL' to stage it in Azure Storage "
        + "instead, which needs a blob data role.\n"
        + "  - In an air-gapped enclave, tick 'skip upload' and supply a pre-staged location.";

    private static string DataPlaneAccessMessage(DeploymentPlan plan, string action, SharedKeyStatus status, string? detail = null) =>
        $"Access denied trying to {action} in storage account '{plan.Artifacts.StorageAccountName}'.\n\n"
        + ExplainUploadFailure(
            status,
            plan.Artifacts.StorageAccountName,
            StagingResourceGroup(plan),
            plan.Azure.SubscriptionId,
            detail);

    /// <summary>
    /// Reads the subscription state that predicts a mid-deployment failure.
    /// </summary>
    /// <remarks>
    /// The permissions and features APIs have no typed surface in the packages this project
    /// references, so they are plain REST calls against the cloud's own ARM endpoint. Everything
    /// that involves a decision lives in <see cref="DeploymentPreflight"/> and is unit tested
    /// without a subscription; this method only gathers facts.
    /// </remarks>
    public async Task<IReadOnlyList<PreflightIssue>> RunPreflightAsync(
        DeploymentPlan plan,
        CancellationToken ct)
    {
        var subscription = GetSubscription(plan);

        // Read permissions at the narrowest scope that exists. A role assigned directly on the
        // resource group is invisible from subscription scope, so checking the wrong one would
        // report a missing role that is actually present. When the group does not exist yet no
        // assignment can exist on it either, so subscription scope is then the complete picture.
        //
        // The group that matters is the one holding the staging account, which is the operator's
        // nominated group when they have set one - checking the lab's group instead would read
        // permissions on a resource group the storage account is not even in.
        var stagingGroup = StagingResourceGroup(plan);

        var groupExists = await subscription.GetResourceGroups()
            .ExistsAsync(stagingGroup, ct);

        var scope = groupExists
            ? $"subscriptions/{plan.Azure.SubscriptionId}/resourceGroups/{stagingGroup}"
            : $"subscriptions/{plan.Azure.SubscriptionId}";

        var scopeLabel = groupExists
            ? $"resource group '{stagingGroup}'"
            : "the subscription";

        var permissions = await ReadPermissionsAsync(scope, ct);

        return DeploymentPreflight.Check(plan, new PreflightFacts(
            permissions.DataActions,
            permissions.NotDataActions,
            await ReadProviderStatesAsync(plan, subscription, ct),
            await ReadEncryptionAtHostAsync(plan, ct),
            scopeLabel,
            permissions.Actions,
            permissions.NotActions,
            await ReadCoresQuotaAsync(plan, ct)));
    }

    /// <summary>
    /// Reads the region's vCPU usage and its size catalogue. Both are needed together: the usage
    /// call gives the limit, and only the size catalogue says what a Standard_D4s_v5 actually
    /// costs against it.
    /// </summary>
    /// <remarks>
    /// Returns null on any failure, so an unreadable quota API cannot block a deployment that
    /// would have worked.
    /// </remarks>
    private async Task<CoresQuota?> ReadCoresQuotaAsync(DeploymentPlan plan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.Azure.Location))
        {
            return null;
        }

        var prefix = $"subscriptions/{plan.Azure.SubscriptionId}/providers/Microsoft.Compute" +
            $"/locations/{Uri.EscapeDataString(plan.Azure.Location)}";

        using var usages = await GetArmJsonAsync($"{prefix}/usages?api-version=2023-07-01", ct);
        if (usages is null || !usages.RootElement.TryGetProperty("value", out var usageList))
        {
            return null;
        }

        int? limit = null;
        int? inUse = null;

        foreach (var usage in usageList.EnumerateArray())
        {
            // "cores" is the Total Regional vCPUs bucket - the one the quota error names. The
            // per-family buckets have names like "standardDSv5Family" and are a separate limit.
            if (!usage.TryGetProperty("name", out var name) ||
                !name.TryGetProperty("value", out var value) ||
                !string.Equals(value.GetString(), "cores", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (usage.TryGetProperty("limit", out var l) && l.TryGetInt32(out var limitValue) &&
                usage.TryGetProperty("currentValue", out var c) && c.TryGetInt32(out var usedValue))
            {
                limit = limitValue;
                inUse = usedValue;
            }

            break;
        }

        if (limit is not { } approved || inUse is not { } consumed)
        {
            return null;
        }

        using var sizes = await GetArmJsonAsync($"{prefix}/vmSizes?api-version=2023-07-01", ct);
        var coresBySize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (sizes is not null && sizes.RootElement.TryGetProperty("value", out var sizeList))
        {
            foreach (var size in sizeList.EnumerateArray())
            {
                if (size.TryGetProperty("name", out var name) &&
                    name.GetString() is { Length: > 0 } sizeName &&
                    size.TryGetProperty("numberOfCores", out var cores) &&
                    cores.TryGetInt32(out var coreCount))
                {
                    coresBySize[sizeName] = coreCount;
                }
            }
        }

        return new CoresQuota(approved, consumed, coresBySize);
    }

    private async Task<(IReadOnlyList<string>? DataActions, IReadOnlyList<string> NotDataActions,
        IReadOnlyList<string>? Actions, IReadOnlyList<string> NotActions)> ReadPermissionsAsync(
        string scope,
        CancellationToken ct)
    {
        var json = await GetArmJsonAsync(
            $"{scope}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", ct);

        // Null, not empty: an unreadable permissions API must not look like a missing role.
        if (json is null)
        {
            return (null, [], null, []);
        }

        using (json)
        {
            List<string> data = [], notData = [], actions = [], notActions = [];

            if (!json.RootElement.TryGetProperty("value", out var value))
            {
                return (null, [], null, []);
            }

            foreach (var permission in value.EnumerateArray())
            {
                data.AddRange(ReadStrings(permission, "dataActions"));
                notData.AddRange(ReadStrings(permission, "notDataActions"));
                actions.AddRange(ReadStrings(permission, "actions"));
                notActions.AddRange(ReadStrings(permission, "notActions"));
            }

            return (data, notData, actions, notActions);
        }

        static IEnumerable<string> ReadStrings(JsonElement element, string name) =>
            element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0)
                : [];
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadProviderStatesAsync(
        DeploymentPlan plan,
        SubscriptionResource subscription,
        CancellationToken ct)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in DeploymentPreflight.RequiredProviders(plan))
        {
            try
            {
                var provider = await subscription.GetResourceProviderAsync(name, cancellationToken: ct);
                if (provider.Value.Data.RegistrationState is { Length: > 0 } state)
                {
                    states[name] = state;
                }
            }
            catch (RequestFailedException)
            {
                // Unreadable is not the same as unregistered; leave it out rather than guess.
            }
        }

        return states;
    }

    private async Task<bool?> ReadEncryptionAtHostAsync(DeploymentPlan plan, CancellationToken ct)
    {
        if (plan.Mlz is not { Enabled: true })
        {
            return null;
        }

        var json = await GetArmJsonAsync(
            $"subscriptions/{plan.Azure.SubscriptionId}/providers/Microsoft.Features" +
            "/featureProviders/Microsoft.Compute/features/EncryptionAtHost?api-version=2021-07-01",
            ct);

        if (json is null)
        {
            return null;
        }

        using (json)
        {
            return json.RootElement.TryGetProperty("properties", out var properties) &&
                   properties.TryGetProperty("state", out var state)
                ? string.Equals(state.GetString(), "Registered", StringComparison.OrdinalIgnoreCase)
                : null;
        }
    }

    /// <summary>
    /// A bare authenticated GET against ARM. Returns null when the answer cannot be had, which the
    /// callers treat as "unknown" rather than "bad" - a tenant may deny these read APIs to an
    /// account that can still deploy perfectly well, and a preflight must never be the reason a
    /// working deployment is refused.
    /// </summary>
    private async Task<JsonDocument?> GetArmJsonAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var scope = AzureCloudEndpoints.GetResourceManagerDefaultScope(Cloud);
            var token = await _credential.GetTokenAsync(new TokenRequestContext([scope]), ct);

            var endpoint = AzureCloudEndpoints.GetResourceManagerEndpoint(Cloud);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await PreflightHttpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static readonly HttpClient PreflightHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string?> ResolveDedicatedHostIdAsync(DeploymentPlan plan, CancellationToken ct)
    {
        if (plan.DedicatedHost is not { Enabled: true } spec)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(spec.HostId))
        {
            return spec.HostId;
        }

        var resourceGroup = await GetSubscription(plan)
            .GetResourceGroups()
            .GetAsync(plan.Azure.ResourceGroup, ct);

        var hostGroup = await resourceGroup.Value
            .GetDedicatedHostGroups()
            .GetAsync(spec.HostGroupName, cancellationToken: ct);

        await foreach (var host in hostGroup.Value.GetDedicatedHosts().GetAllAsync(cancellationToken: ct))
        {
            if (string.IsNullOrWhiteSpace(spec.Sku) ||
                string.Equals(host.Data.Sku?.Name, spec.Sku, StringComparison.OrdinalIgnoreCase))
            {
                return host.Id.ToString();
            }
        }

        throw new InvalidOperationException(
            $"No dedicated host with SKU '{spec.Sku}' found in host group '{spec.HostGroupName}'.");
    }

    public Task<ArmDeploymentOutcome> DeployTemplateAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct) =>
        RunDeploymentAsync(plan, deploymentName, template, parameters, validateOnly: false, ct);

    public Task<ArmDeploymentOutcome> ValidateTemplateAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct) =>
        RunDeploymentAsync(plan, deploymentName, template, parameters, validateOnly: true, ct);

    private async Task<ArmDeploymentOutcome> RunDeploymentAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        bool validateOnly,
        CancellationToken ct)
    {
        var subscription = GetSubscription(plan);
        var isSubscriptionScoped = template.Scope == ArmDeploymentScope.Subscription;

        // The SDK marks these obsolete in favour of Azure.ResourceManager.Resources.Deployments,
        // but that namespace does not exist in any Azure.ResourceManager.Resources version
        // available on our feed. Note that we are pinned to 1.11.2 precisely because 1.12.0
        // deprecated these types without keeping them in its ModelReaderWriterContext, which
        // breaks every deployment at the point the LRO completes. See the csproj for the detail.
#pragma warning disable CS0618
        var content = new ArmDeploymentContent(
            new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(template.ReadContent()),
                Parameters = BinaryData.FromString(ToArmParameterJson(parameters))
            })
        {
            // Required at subscription scope, and rejected at resource group scope, where the
            // deployment inherits the group's location.
            Location = isSubscriptionScoped ? new AzureLocation(plan.Azure.Location) : null
        };

        // ARM deployment names must be unique within their scope; suffix so re-runs
        // do not collide with earlier attempts.
        var uniqueName = $"{Sanitize(deploymentName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        ArmDeploymentCollection deployments;
        string scopeId;

        if (isSubscriptionScoped)
        {
            deployments = subscription.GetArmDeployments();
            scopeId = $"/subscriptions/{plan.Azure.SubscriptionId}";
        }
        else
        {
            var resourceGroup = await subscription.GetResourceGroups()
                .GetAsync(plan.Azure.ResourceGroup, ct);

            deployments = resourceGroup.Value.GetArmDeployments();
            scopeId = $"/subscriptions/{plan.Azure.SubscriptionId}/resourceGroups/{plan.Azure.ResourceGroup}";
        }

        if (validateOnly)
        {
            var deploymentId = ArmDeploymentResource.CreateResourceIdentifier(scopeId, uniqueName);

            var validation = await _client.GetArmDeploymentResource(deploymentId)
                .ValidateAsync(WaitUntil.Completed, content, ct);

            var error = validation.Value.Error;

            return new ArmDeploymentOutcome(
                uniqueName,
                error is null ? "Succeeded" : "Failed",
                error is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["error"] = $"{error.Code}: {error.Message}" });
        }

        ArmDeploymentPropertiesExtended properties;
        try
        {
            var operation = await deployments.CreateOrUpdateAsync(WaitUntil.Completed, uniqueName, content, ct);
            properties = operation.Value.Data.Properties;
        }
        catch (RequestFailedException ex)
        {
            // ARM reports a failed template as "At least one resource deployment operation failed",
            // which says nothing about which resource or why. The detail is only available by
            // asking for the deployment's individual operations afterwards, so do that here rather
            // than leaving the operator to go and run `az deployment operation list` by hand.
            var detail = await DescribeFailedOperationsAsync(scopeId, uniqueName, ct);

            // A policy denial never reaches the operation list: ARM rejects the whole template up
            // front, so there are no operations to read back. The reason is only in this exception.
            detail ??= ExplainPolicyDenial(ex.Message);

            throw detail is null
                ? ex
                : new InvalidOperationException($"{detail} (deployment '{uniqueName}')", ex);
        }
#pragma warning restore CS0618

        return new ArmDeploymentOutcome(
            uniqueName,
            properties.ProvisioningState?.ToString() ?? "Unknown",
            ParseOutputs(properties.Outputs));
    }

    /// <summary>
    /// Reads back the resource-level failures for a deployment that ARM only described in general
    /// terms. Nested deployments are followed one level down, because template-linked resources
    /// (Bastion's public IP, for example) report their real error there rather than at the top.
    /// </summary>
    /// <returns>A human-readable summary, or null if nothing more specific could be found.</returns>
    private async Task<string?> DescribeFailedOperationsAsync(
        string scopeId,
        string deploymentName,
        CancellationToken ct)
    {
        try
        {
            var failures = new List<string>();
            await CollectFailedOperationsAsync(scopeId, deploymentName, failures, depth: 0, ct);

            if (failures.Count == 0)
            {
                return null;
            }

            return failures.Count == 1
                ? failures[0]
                : string.Join(" | ", failures.Take(MaxReportedFailures));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let the diagnostic replace the original failure with one of its own.
            return null;
        }
    }

    private const int MaxReportedFailures = 5;

    private async Task CollectFailedOperationsAsync(
        string scopeId,
        string deploymentName,
        List<string> failures,
        int depth,
        CancellationToken ct)
    {
#pragma warning disable CS0618
        var deploymentId = ArmDeploymentResource.CreateResourceIdentifier(scopeId, deploymentName);
        var deployment = _client.GetArmDeploymentResource(deploymentId);

        await foreach (var operation in deployment.GetDeploymentOperationsAsync(cancellationToken: ct))
        {
            if (failures.Count >= MaxReportedFailures)
            {
                return;
            }

            var op = operation.Properties;
            if (op is null ||
                !string.Equals(op.ProvisioningState, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = op.TargetResource;
            var resourceType = target?.ResourceType?.ToString();

            // A failed nested deployment is a signpost, not a cause: recurse for the real error.
            if (depth == 0 &&
                target?.ResourceName is { Length: > 0 } nested &&
                string.Equals(resourceType, "Microsoft.Resources/deployments", StringComparison.OrdinalIgnoreCase))
            {
                await CollectFailedOperationsAsync(scopeId, nested, failures, depth + 1, ct);
                continue;
            }

            var error = op.StatusMessage?.Error;
            var where = target?.ResourceName is { Length: > 0 } name
                ? $"{resourceType ?? "resource"} '{name}'"
                : resourceType ?? "resource";

            var why = error is not null
                ? $"{error.Code}: {error.Message}"
                : op.StatusCode is { Length: > 0 } status
                    ? status
                    : "failed with no error detail";

            failures.Add($"{where} - {why}");
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Turns an Azure Policy denial into the change that would make the deployment succeed.
    /// </summary>
    /// <remarks>
    /// ARM refuses a policy-denied template before it creates anything, so
    /// <see cref="DescribeFailedOperationsAsync"/> finds no operations to explain it. The reason
    /// is buried in a 2 KB JSON body that names the policy definition by GUID and lists every
    /// evaluated expression - accurate but unreadable, and it does not say what to do about it.
    /// </remarks>
    internal static string? ExplainPolicyDenial(string? message)
    {
        if (string.IsNullOrEmpty(message) ||
            !message.Contains("RequestDisallowedByPolicy", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var reasons = System.Text.RegularExpressions.Regex
            .Matches(message, @"Reasons: '([^']*)'")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var text = reasons.Count > 0
            ? $"Azure Policy refused this step, so nothing in it was created. The policy says: {string.Join(" ", reasons)}"
            : "Azure Policy refused this step, so nothing in it was created.";

        if (message.Contains("publicIpAddress", StringComparison.OrdinalIgnoreCase))
        {
            text +=
                " That is the per-VM public IP. Clear 'Give each VM a public IP' on the Network page " +
                "and deploy again - the lab does not need one, because Azure Bastion reaches the VMs " +
                "over the virtual network. Enable Bastion on the same page if it is off, or you will " +
                "have built machines you cannot sign in to.";
        }
        else
        {
            text +=
                " This is a tenant or management-group assignment, not something this tool can " +
                "override; the resource has to change or the policy needs an exemption.";
        }

        return text;
    }

    private static string ToArmParameterJson(IReadOnlyDictionary<string, object?> parameters)
    {
        var wrapped = parameters.ToDictionary(
            kvp => kvp.Key,
            kvp => (object)new Dictionary<string, object?> { ["value"] = kvp.Value });

        return JsonSerializer.Serialize(wrapped);
    }

    private static IReadOnlyDictionary<string, object?> ParseOutputs(BinaryData? outputs)
    {
        var result = new Dictionary<string, object?>();
        if (outputs is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(outputs);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.TryGetProperty("value", out var value)
                ? value.ToString()
                : property.Value.ToString();
        }

        return result;
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}
