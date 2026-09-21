using System.Text.RegularExpressions;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Validation;

public enum PreflightSeverity
{
    /// <summary>The deployment cannot succeed. Nothing is created.</summary>
    Blocking,

    /// <summary>Worth knowing, but the deployment is allowed to proceed.</summary>
    Warning
}

/// <param name="Title">Short statement of what is wrong.</param>
/// <param name="Detail">What to do about it, in the operator's terms.</param>
public sealed record PreflightIssue(PreflightSeverity Severity, string Title, string Detail)
{
    public override string ToString() => $"{Title} - {Detail}";
}

/// <summary>
/// Everything the preflight needs to know about the subscription, gathered by the caller so the
/// decisions themselves can be tested without a subscription.
/// </summary>
/// <param name="DataActions">
/// Data-plane actions the signed-in principal has at the target scope, or null when the
/// permissions API could not be read. Null is <em>not</em> the same as an empty list: a tenant can
/// deny this read to an account that is still perfectly able to deploy, and treating that as "no
/// permission" would block a deployment that would have worked.
/// </param>
/// <param name="NotDataActions">Data-plane actions explicitly subtracted.</param>
/// <param name="Actions">
/// Control-plane actions the signed-in principal has at the target scope, or null when the
/// permissions API could not be read. Needed because listing the staging account's keys is a
/// control-plane action, and having it means the missing blob data role is survivable.
/// </param>
/// <param name="NotActions">Control-plane actions explicitly subtracted.</param>
/// <param name="ProviderStates">
/// Resource provider namespace to registration state. Empty when they could not be read.
/// </param>
/// <param name="EncryptionAtHostRegistered">
/// Null when the feature was not queried or could not be read, which is the case unless MLZ is
/// enabled.
/// </param>
/// <param name="ScopeChecked">Scope the permissions were read at, for the error text.</param>
/// <param name="Cores">
/// Regional vCPU quota for the target location, or null when it could not be read.
/// </param>
public sealed record PreflightFacts(
    IReadOnlyList<string>? DataActions,
    IReadOnlyList<string> NotDataActions,
    IReadOnlyDictionary<string, string> ProviderStates,
    bool? EncryptionAtHostRegistered,
    string ScopeChecked,
    IReadOnlyList<string>? Actions = null,
    IReadOnlyList<string>? NotActions = null,
    CoresQuota? Cores = null);

/// <summary>
/// The subscription's regional vCPU quota and what each VM size in the plan costs against it.
/// </summary>
/// <param name="Limit">Approved Total Regional vCPUs for the location.</param>
/// <param name="InUse">vCPUs already consumed in the location, by anything in the subscription.</param>
/// <param name="CoresBySize">
/// VM size name to vCPU count, from the location's size catalogue. A size missing from the map is
/// one this check cannot price, which is why a shortfall is only reported when the sizes it
/// <em>could</em> price already exceed what is left.
/// </param>
public sealed record CoresQuota(
    int Limit,
    int InUse,
    IReadOnlyDictionary<string, int> CoresBySize);

/// <summary>
/// Checks the things that cannot be seen in a plan file but reliably break a deployment, so they
/// are reported before anything is created.
/// </summary>
/// <remarks>
/// <para>
/// Both of the first real deployments failed part way through, leaving a resource group and in one
/// case a virtual network behind: once on the blob data-plane role and once on a resource that
/// could not be created. Neither needed a single resource to exist in order to be predicted - the
/// role assignment and the provider registrations are both readable up front.
/// </para>
/// <para>
/// The blob role is the one that keeps catching people because Azure's own portal language invites
/// the mistake: subscription <em>Owner</em> sounds total, and it grants nothing on blob data. The
/// staging account is also created fresh each run with a random name, so a role granted on one
/// resource group does nothing for the next deployment into another.
/// </para>
/// </remarks>
public static class DeploymentPreflight
{
    /// <summary>Writing the DSC package is the operation that actually fails without the role.</summary>
    public const string BlobWriteDataAction =
        "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write";

    /// <summary>
    /// Listing the staging account's keys. Included in both Owner and Contributor (their
    /// <c>actions</c> is <c>*</c>, and the exclusions are all under Microsoft.Authorization), so an
    /// account that cannot write blob data can almost always still stage the package with a key.
    /// </summary>
    public const string ListKeysAction = "Microsoft.Storage/storageAccounts/listkeys/action";

    public static IReadOnlyList<PreflightIssue> Check(DeploymentPlan plan, PreflightFacts facts)
    {
        var issues = new List<PreflightIssue>();

        CheckBlobDataRole(plan, facts, issues);
        CheckResourceProviders(plan, facts, issues);
        CheckEncryptionAtHost(plan, facts, issues);
        CheckRegionalCoreQuota(plan, facts, issues);

        return issues;
    }

    /// <summary>
    /// Every vCPU the plan will ask for, counted the way Azure counts them.
    /// </summary>
    /// <remarks>
    /// AVD session hosts are included: they are ordinary virtual machines and there can be several
    /// of them, so leaving them out would under-count the largest part of some plans.
    /// </remarks>
    public static (int Cores, IReadOnlyList<string> UnpricedSizes) RequiredCores(
        DeploymentPlan plan,
        IReadOnlyDictionary<string, int> coresBySize)
    {
        var total = 0;
        var unpriced = new List<string>();

        void Add(string size, int count)
        {
            if (coresBySize.TryGetValue(size, out var cores))
            {
                total += cores * count;
            }
            else if (!unpriced.Contains(size, StringComparer.OrdinalIgnoreCase))
            {
                unpriced.Add(size);
            }
        }

        foreach (var server in plan.EnabledServers)
        {
            Add(server.VmSize, 1);
        }

        if (plan.Avd is { Enabled: true } avd && avd.SessionHostCount > 0)
        {
            Add(avd.VmSize, avd.SessionHostCount);
        }

        return (total, unpriced);
    }

    /// <summary>
    /// Regional vCPU quota. Azure refuses each virtual machine individually at preflight, so a
    /// plan that is two cores over its limit reports the same wall of text once per server -
    /// after the resource group and the network have already been built.
    /// </summary>
    /// <remarks>
    /// Only reported when the sizes that could be priced already exceed what is left, so a size
    /// missing from the location catalogue softens the check rather than inventing a shortfall.
    /// The quota is per region and counts every virtual machine in the subscription, not just
    /// this lab's - which is exactly why it cannot be predicted from the plan alone.
    /// </remarks>
    private static void CheckRegionalCoreQuota(
        DeploymentPlan plan,
        PreflightFacts facts,
        List<PreflightIssue> issues)
    {
        if (facts.Cores is not { } quota || quota.Limit <= 0)
        {
            return;
        }

        var (required, unpriced) = RequiredCores(plan, quota.CoresBySize);
        if (required == 0)
        {
            return;
        }

        var available = quota.Limit - quota.InUse;
        if (required <= available)
        {
            return;
        }

        var partial = unpriced.Count > 0
            ? $" This is already over the limit without counting {string.Join(", ", unpriced)}, " +
              "whose size the region did not report, so the real shortfall is larger."
            : "";

        issues.Add(new PreflightIssue(
            PreflightSeverity.Blocking,
            $"Not enough vCPU quota in {plan.Azure.Location}",
            $"This plan needs {required} vCPUs and only {Math.Max(available, 0)} of the " +
            $"{quota.Limit} approved for {plan.Azure.Location} are free ({quota.InUse} in use by " +
            $"this subscription already, including labs built earlier).{partial} Azure refuses each " +
            "virtual machine separately, so without this check the run would build the resource " +
            "group and network and then fail once per server. Either delete something, build the " +
            "lab in another region, choose smaller VM sizes, or request more quota at " +
            "Subscription > Usage + quotas in the portal."));
    }

    /// <summary>
    /// Resource providers this plan will touch. Registration is normally automatic on first use,
    /// but it is a subscription-level write that a locked-down or newly created subscription can
    /// refuse - and the failure then appears against an individual resource, minutes in.
    /// </summary>
    public static IReadOnlyList<string> RequiredProviders(DeploymentPlan plan)
    {
        var providers = new List<string>
        {
            "Microsoft.Resources",
            "Microsoft.Network",
            "Microsoft.Compute"
        };

        if (plan.Artifacts.StagesInAzureStorage)
        {
            providers.Add("Microsoft.Storage");
        }

        if (plan.Avd is { Enabled: true })
        {
            providers.Add("Microsoft.DesktopVirtualization");
        }

        if (plan.Identity.AdminPasswordSecret is not null)
        {
            providers.Add("Microsoft.KeyVault");
        }

        if (plan.Mlz is { Enabled: true } mlz)
        {
            providers.Add("Microsoft.OperationalInsights");

            if (mlz.DeployDefender || mlz.DeploySentinel)
            {
                providers.Add("Microsoft.Security");
            }

            if (mlz.DeployPolicy)
            {
                providers.Add("Microsoft.PolicyInsights");
            }
        }

        return providers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CheckBlobDataRole(
        DeploymentPlan plan,
        PreflightFacts facts,
        List<PreflightIssue> issues)
    {
        // Nothing is uploaded in a pre-staged run, nor when the package is fetched straight from
        // its published URL, so the role is irrelevant in both cases.
        if (!plan.Artifacts.StagesInAzureStorage)
        {
            return;
        }

        // Null means the permissions API could not be read, not that the role is absent.
        if (facts.DataActions is not { } dataActions)
        {
            return;
        }

        if (HasAction(dataActions, facts.NotDataActions, BlobWriteDataAction))
        {
            return;
        }

        // Missing the data role is only fatal if the key path is also unavailable. Owner and
        // Contributor can both list the staging account's keys, and the account is created by this
        // tool moments earlier, so staging can proceed with a key-signed SAS instead. Blocking here
        // regardless - which is what this used to do - stopped deployments that would have worked.
        if (facts.Actions is null
            || HasAction(facts.Actions, facts.NotActions ?? [], ListKeysAction))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Warning,
                "Staging the DSC package with an account key",
                $"The signed-in account cannot write blob data at {facts.ScopeChecked} - subscription " +
                "Owner and Contributor are control-plane only and grant no access to blob data. The " +
                "package will instead be staged using the staging account's own access key, which " +
                "Owner and Contributor can read, so no action is needed. To use Entra credentials " +
                "for staging instead, grant the signed-in account 'Storage Blob Data Contributor'."));
            return;
        }

        issues.Add(new PreflightIssue(
            PreflightSeverity.Blocking,
            "Cannot stage the DSC package",
            $"The signed-in account can neither write blob data nor read storage account keys at " +
            $"{facts.ScopeChecked}, so staging the DSC package would fail after the resource group " +
            "and network had already been created. Grant 'Storage Blob Data Contributor' - at " +
            "subscription scope is simplest, because the staging account is created fresh with a " +
            "new name on every run, so a grant on one resource group does nothing for the next. " +
            "Role assignments can take a few minutes to take effect. Alternatively set " +
            "artifacts.skipUpload with artifactsLocationOverride and artifactsSasTokenOverride to " +
            "pre-stage the package."));
    }

    private static void CheckResourceProviders(
        DeploymentPlan plan,
        PreflightFacts facts,
        List<PreflightIssue> issues)
    {
        // An empty map means the providers could not be read, which is not the same as unregistered.
        if (facts.ProviderStates.Count == 0)
        {
            return;
        }

        foreach (var provider in RequiredProviders(plan))
        {
            if (!facts.ProviderStates.TryGetValue(provider, out var state))
            {
                continue;
            }

            if (state.Equals("Registered", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Registering", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            issues.Add(new PreflightIssue(
                PreflightSeverity.Blocking,
                $"Resource provider '{provider}' is not registered",
                $"The subscription reports '{provider}' as {state}. Register it with " +
                $"'az provider register --namespace {provider}' (or Subscription > Resource " +
                "providers in the portal) and wait for it to reach Registered. Registration is " +
                "usually automatic, but a locked-down subscription can refuse it, and the failure " +
                "would otherwise appear against an individual resource minutes into the run."));
        }
    }

    private static void CheckEncryptionAtHost(
        DeploymentPlan plan,
        PreflightFacts facts,
        List<PreflightIssue> issues)
    {
        if (plan.Mlz is not { Enabled: true } || facts.EncryptionAtHostRegistered is not false)
        {
            return;
        }

        issues.Add(new PreflightIssue(
            PreflightSeverity.Blocking,
            "'EncryptionAtHost' is not registered on this subscription",
            "Mission Landing Zone builds virtual machines with encryption at host enabled, which " +
            "requires the Microsoft.Compute/EncryptionAtHost feature to be registered first. Run " +
            "'az feature register --namespace Microsoft.Compute --name EncryptionAtHost', wait for " +
            "it to show Registered, then 'az provider register --namespace Microsoft.Compute' to " +
            "propagate it. This can take several minutes and cannot be done mid-deployment."));
    }

    /// <summary>
    /// Azure action strings use <c>*</c> as a wildcard that spans segment separators, so
    /// <c>Microsoft.Storage/*</c> covers the blob data action. notDataActions subtract from
    /// dataActions, and a subtraction wins.
    /// </summary>
    internal static bool HasAction(
        IReadOnlyList<string> actions,
        IReadOnlyList<string> notActions,
        string required) =>
        actions.Any(a => Matches(a, required)) && !notActions.Any(a => Matches(a, required));

    private static bool Matches(string pattern, string action)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        if (!pattern.Contains('*'))
        {
            return string.Equals(pattern, action, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(action, regex, RegexOptions.IgnoreCase);
    }
}
