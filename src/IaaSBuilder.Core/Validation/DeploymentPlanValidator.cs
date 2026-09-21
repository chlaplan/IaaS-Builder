using System.Net;
using System.Text.RegularExpressions;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;

namespace IaaSBuilder.Core.Validation;

/// <summary>
/// Validates a plan before anything is deployed.
/// </summary>
/// <remarks>
/// The legacy script validated exactly one thing (password complexity, on a text box's
/// LostFocus) and discovered everything else the hard way: an IP outside the subnet, a
/// 20-character computer name or an invalid storage account name surfaced as an ARM failure
/// several minutes into a build, after resources had already been created.
/// </remarks>
public sealed class DeploymentPlanValidator
{
    private readonly ISet<string>? _availableDscTokens;

    /// <summary>Mission Landing Zone's <c>identifier</c>: 1-5 alphanumeric characters.</summary>
    private static readonly Regex MlzIdentifierPattern = new("^[a-zA-Z0-9]{1,5}$", RegexOptions.Compiled);

    /// <param name="availableDscTokens">
    /// Optional set of configuration names present in the DSC package (without the
    /// "Configuration.ps1" suffix). When supplied, roles pointing at a missing DSC script
    /// are reported. See <see cref="Dsc.DscPackageInspector"/>.
    /// </param>
    public DeploymentPlanValidator(ISet<string>? availableDscTokens = null) =>
        _availableDscTokens = availableDscTokens;

    public ValidationResult Validate(DeploymentPlan plan, DeploymentSecrets? secrets = null)
    {
        var issues = new List<ValidationIssue>();

        ValidateTarget(plan, issues);
        ValidateIdentity(plan, secrets, issues);
        ValidateArtifacts(plan, issues);
        var subnet = ValidateNetwork(plan, issues);
        ValidateServers(plan, subnet, issues);
        ValidateAvd(plan, issues);
        ValidateSaca(plan, issues);
        ValidateMlz(plan, issues);

        return new ValidationResult(issues);
    }

    private static void ValidateTarget(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        // Caught here rather than at sign-in: a plan authored on a connected workstation can be
        // carried into an enclave that has no cloud.json, and the failure would otherwise be an
        // InvalidOperationException part way through building the ARM client.
        if (plan.Azure.Cloud == AzureCloud.Custom && !Azure.CustomCloud.IsConfigured)
        {
            issues.Add(ValidationIssue.Error("azure.cloud",
                $"This plan targets a custom cloud, but no {Azure.CustomCloud.FileName} was found " +
                "beside the application. It must define authorityHost, resourceManagerEndpoint, " +
                "resourceManagerAudience and blobSuffix."));
        }

        if (string.IsNullOrWhiteSpace(plan.Azure.SubscriptionId))
            issues.Add(ValidationIssue.Error("azure.subscriptionId", "A subscription must be selected."));
        else if (!Guid.TryParse(plan.Azure.SubscriptionId, out _))
            issues.Add(ValidationIssue.Error("azure.subscriptionId", "Must be a GUID."));

        if (string.IsNullOrWhiteSpace(plan.Azure.Location))
            issues.Add(ValidationIssue.Error("azure.location", "A region must be selected."));

        if (!AzureNaming.IsValidResourceGroupName(plan.Azure.ResourceGroup))
        {
            issues.Add(ValidationIssue.Error("azure.resourceGroup",
                "Must be 1-90 characters of letters, digits or hyphens, and cannot end with a hyphen."));
        }
    }

    private static void ValidateIdentity(DeploymentPlan plan, DeploymentSecrets? secrets, List<ValidationIssue> issues)
    {
        if (!AzureNaming.IsValidDomainName(plan.Identity.DomainName))
        {
            issues.Add(ValidationIssue.Error("identity.domainName",
                "Must be a fully qualified DNS domain name, e.g. contoso.local."));
        }

        if (string.IsNullOrWhiteSpace(plan.Identity.AdminUsername))
        {
            issues.Add(ValidationIssue.Error("identity.adminUsername", "An administrator username is required."));
        }
        else if (IsReservedAdminName(plan.Identity.AdminUsername))
        {
            issues.Add(ValidationIssue.Error("identity.adminUsername",
                $"'{plan.Identity.AdminUsername}' is reserved by Azure and cannot be used."));
        }

        if (secrets is null)
        {
            return;
        }

        if (!secrets.HasAdminPassword && plan.Identity.AdminPasswordSecret is null)
        {
            issues.Add(ValidationIssue.Error("secrets.adminPassword",
                "An administrator password, or a Key Vault reference to one, is required."));
            return;
        }

        if (secrets.HasAdminPassword)
        {
            foreach (var failure in PasswordPolicy.Check(secrets.AdminPassword, plan.Identity.AdminUsername))
            {
                issues.Add(ValidationIssue.Error("secrets.adminPassword", failure));
            }
        }
    }

    private static void ValidateArtifacts(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        // Deliberately ahead of the early returns below. Those two are about how the DSC package
        // itself is staged; where the VMs then get Configuration Manager, Exchange and the rest is
        // a separate question and applies to every staging mode.
        ValidateInstallMedia(plan, issues);

        if (plan.Artifacts.SkipUpload)
        {
            if (string.IsNullOrWhiteSpace(plan.Artifacts.ArtifactsLocationOverride))
            {
                issues.Add(ValidationIssue.Error("artifacts.artifactsLocationOverride",
                    "Uploading is disabled, so a pre-staged artifact location must be supplied."));
            }
            else if (string.IsNullOrWhiteSpace(plan.Artifacts.ArtifactsSasTokenOverride)
                     && plan.Artifacts.ArtifactsLocationOverride.Contains(".blob.", StringComparison.OrdinalIgnoreCase))
            {
                // A warning rather than an error: an internal artifact server, or a container
                // deliberately left anonymously readable, is legitimate. But a private blob is the
                // default, and the failure lands inside the VM long after ARM reports success.
                issues.Add(ValidationIssue.Warning("artifacts.artifactsSasTokenOverride",
                    "No SAS token was supplied for the pre-staged blob location. Unless that container " +
                    "allows anonymous read, the DSC extension will fail with 403 after the VMs build."));
            }

            return;
        }

        // Fetched straight from a URL: no storage account is involved, so none of the account,
        // container or staging-group rules below apply.
        if (plan.Artifacts.UsePublicPackageUrl)
        {
            if (!PublicPackageSource.TrySplit(plan.Artifacts.PublicPackageUrl, out _, out var fileName))
            {
                issues.Add(ValidationIssue.Error("artifacts.publicPackageUrl",
                    "Must be a direct HTTPS link ending in the package file itself, for example " +
                    PublicPackageSource.DefaultUrl));
            }
            else if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // A GitHub /blob/ or /tree/ link serves an HTML page. The extension would download
                // that page, fail to open it as an archive, and report it long after the VMs built.
                issues.Add(ValidationIssue.Warning("artifacts.publicPackageUrl",
                    $"The URL ends in '{fileName}' rather than a .zip. The DSC extension needs the " +
                    "package file itself - a link to a web page about it will download the page."));
            }

            return;
        }

        if (!AzureNaming.IsValidStorageAccountName(plan.Artifacts.StorageAccountName))
        {
            issues.Add(ValidationIssue.Error("artifacts.storageAccountName",
                "Must be 3-24 characters of lowercase letters and digits only."));
        }

        if (!AzureNaming.IsValidBlobContainerName(plan.Artifacts.ContainerName))
        {
            issues.Add(ValidationIssue.Error("artifacts.containerName",
                "Must be 3-63 characters of lowercase letters, digits and single hyphens, " +
                "starting and ending with a letter or digit."));
        }

        // Optional: empty means the staging account is created in the lab's own resource group.
        // Only the name is checked here - whether the account exists in it cannot be known without
        // a subscription, and is reported by the deployment itself before anything is created.
        if (!string.IsNullOrWhiteSpace(plan.Artifacts.StorageResourceGroup)
            && !AzureNaming.IsValidResourceGroupName(plan.Artifacts.StorageResourceGroup))
        {
            issues.Add(ValidationIssue.Error("artifacts.storageResourceGroup",
                "Must be a valid resource group name, or empty to create the staging account in " +
                "the lab's own resource group."));
        }

        // Only checked when the tool is the thing doing the uploading. With SkipUpload set the
        // package never leaves this machine, so its path is irrelevant - which is why this sits
        // after the early return above.
        if (string.IsNullOrWhiteSpace(plan.Artifacts.DscPackagePath))
        {
            issues.Add(ValidationIssue.Error("artifacts.dscPackagePath",
                "A DSC package path is required in order to upload one."));
        }
    }

    /// <summary>
    /// Checks the location the VMs will take installer media from.
    /// </summary>
    /// <remarks>
    /// Typing this wrong is expensive. Nothing reads the value until a DSC configuration is running
    /// inside a VM, which is minutes to hours after the deployment has been reported as successful,
    /// so a malformed location is not discovered until the lab has already been built and paid for.
    /// That is worth a few cheap rules here.
    /// </remarks>
    private static void ValidateInstallMedia(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        // The path is repeated at each call site rather than held in a local, because
        // FieldValidationPathTests scans this source for the literal in order to prove the UI
        // field can actually turn red. A local would hide it and the guard would go quiet.
        var location = plan.Artifacts.InstallMediaLocation.Trim();

        if (location.Length == 0)
        {
            // Nothing to validate, but a fall-back with nothing to fall back from is always a
            // mistake - usually a half-finished edit - and silently means nothing at all.
            if (plan.Artifacts.InstallMediaFallbackToInternet)
            {
                issues.Add(ValidationIssue.Warning("artifacts.installMediaFallbackToInternet",
                    "Falling back to the Microsoft downloads is switched on, but no install media " +
                    "location was given, so there is nothing to fall back from. Either name a " +
                    "location or turn this off."));
            }

            return;
        }

        var isWeb = location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var isUnc = location.StartsWith(@"\\", StringComparison.Ordinal);

        // A drive-qualified local path such as D:\installers. Relative paths are rejected because
        // the scripts run from several different working directories - Task Scheduler uses one,
        // the DSC extension another - so a relative path means different things to different roles.
        var isLocal = location.Length >= 3
                      && char.IsLetter(location[0])
                      && location[1] == ':'
                      && (location[2] == '\\' || location[2] == '/');

        if (!isWeb && !isUnc && !isLocal)
        {
            issues.Add(ValidationIssue.Error("artifacts.installMediaLocation",
                "Must be an https or http URL, a UNC share such as \\\\server\\share\\installers, " +
                "or a drive-qualified local path such as D:\\installers. A relative path will not " +
                "work: the install scripts run from several different working directories."));
            return;
        }

        if (isWeb && !Uri.TryCreate(location, UriKind.Absolute, out _))
        {
            issues.Add(ValidationIssue.Error("artifacts.installMediaLocation", "This does not parse as a URL."));
            return;
        }

        // The file names are appended to this, so a location that is already a file is a sign the
        // operator has pointed at one installer rather than the folder holding all of them.
        foreach (var suffix in InstallMediaFileSuffixes)
        {
            if (location.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(ValidationIssue.Error("artifacts.installMediaLocation",
                    $"This points at a file, not a folder. The VMs append names like " +
                    $"'{DscInstallMedia.FileNames[0]}' to this location, so it must be the folder " +
                    "that holds all of the staged media."));
                return;
            }
        }
    }

    private static readonly string[] InstallMediaFileSuffixes = [".exe", ".iso", ".zip", ".msi"];

    private static Cidr? ValidateNetwork(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        var network = plan.Network;

        if (string.IsNullOrWhiteSpace(network.Prefix))
            issues.Add(ValidationIssue.Error("network.prefix", "A naming prefix is required."));
        else if (network.Prefix.Length > 8)
            issues.Add(ValidationIssue.Warning("network.prefix",
                "Prefixes longer than 8 characters make it easy to exceed the 15-character computer name limit."));

        if (!Cidr.TryParse(network.AddressPrefix, out var vnet))
        {
            issues.Add(ValidationIssue.Error("network.addressPrefix", "Must be a valid IPv4 CIDR, e.g. 10.0.0.0/16."));
            return null;
        }

        if (!Cidr.TryParse(network.SubnetPrefix, out var subnet))
        {
            issues.Add(ValidationIssue.Error("network.subnetPrefix", "Must be a valid IPv4 CIDR, e.g. 10.0.0.0/24."));
            return null;
        }

        if (!vnet.Contains(subnet))
        {
            issues.Add(ValidationIssue.Error("network.subnetPrefix",
                $"Subnet {subnet} is not inside virtual network {vnet}."));
        }

        if (plan.Bastion.Enabled)
        {
            if (!Cidr.TryParse(plan.Bastion.SubnetPrefix, out var bastion))
            {
                issues.Add(ValidationIssue.Error("bastion.subnetPrefix", "Must be a valid IPv4 CIDR."));
            }
            else
            {
                if (!vnet.Contains(bastion))
                {
                    issues.Add(ValidationIssue.Error("bastion.subnetPrefix",
                        $"Bastion subnet {bastion} is not inside virtual network {vnet}."));
                }

                if (bastion.Overlaps(subnet))
                {
                    issues.Add(ValidationIssue.Error("bastion.subnetPrefix",
                        $"Bastion subnet {bastion} overlaps the workload subnet {subnet}."));
                }

                // Azure requires AzureBastionSubnet to be at least a /26.
                if (bastion.PrefixLength > 26)
                {
                    issues.Add(ValidationIssue.Error("bastion.subnetPrefix",
                        "AzureBastionSubnet must be /26 or larger."));
                }
            }
        }

        // Without either, the VMs build and then nobody can sign in to them - a failure that only
        // shows up once the deployment has succeeded and the operator goes looking for a console.
        if (!plan.Bastion.Enabled && !plan.Network.AssignPublicIps && plan.EnabledServers.Any())
        {
            issues.Add(ValidationIssue.Warning("bastion.enabled",
                "Nothing will be reachable: Bastion is off and the VMs get no public IP. " +
                "Turn on Bastion, or give the VMs public IPs if your tenant policy allows them."));
        }

        if (plan.Network.AssignPublicIps)
        {
            issues.Add(ValidationIssue.Warning("network.assignPublicIps",
                "Public IPs on VM network interfaces are denied by policy in many tenants, which " +
                "rejects the whole VM deployment rather than just the address. Use Bastion unless " +
                "you know this tenant permits them."));
        }

        return subnet;
    }

    private void ValidateServers(DeploymentPlan plan, Cidr? subnet, List<ValidationIssue> issues)
    {
        var enabled = plan.EnabledServers.ToList();

        if (enabled.Count == 0)
        {
            issues.Add(ValidationIssue.Warning("servers", "No servers are enabled; only networking will be deployed."));
        }

        var presentRoles = enabled.Select(s => s.Role).ToHashSet();
        var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenIps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < plan.Servers.Count; i++)
        {
            var server = plan.Servers[i];
            if (!server.Enabled) continue;

            var path = $"servers[{i}]";

            if (!RoleCatalog.TryGet(server.Role, out var definition))
            {
                issues.Add(ValidationIssue.Error($"{path}.role",
                    $"Role '{server.Role}' is no longer supported. Remove this server or change its role."));
                continue;
            }

            if (!AzureNaming.IsValidComputerName(server.Name))
            {
                issues.Add(ValidationIssue.Error($"{path}.name",
                    $"'{server.Name}' is not a valid Windows computer name " +
                    $"(1-{AzureNaming.MaxWindowsComputerNameLength} characters, letters/digits/hyphens, not all digits)."));
            }

            if (seenNames.TryGetValue(server.Name, out var firstNameIndex))
            {
                issues.Add(ValidationIssue.Error($"{path}.name",
                    $"Duplicate computer name '{server.Name}' (also used by servers[{firstNameIndex}])."));
            }
            else if (!string.IsNullOrWhiteSpace(server.Name))
            {
                seenNames[server.Name] = i;
            }

            ValidateServerAddress(server, subnet, path, seenIps, i, issues);

            if (string.IsNullOrWhiteSpace(server.VmSize))
                issues.Add(ValidationIssue.Error($"{path}.vmSize", "A VM size is required."));

            if (_availableDscTokens is not null && !_availableDscTokens.Contains(definition.DscToken))
            {
                issues.Add(ValidationIssue.Error($"{path}.role",
                    $"Role '{definition.DisplayName}' needs '{definition.DscToken}Configuration.ps1', " +
                    "which is not present in the DSC package."));
            }

            foreach (var required in definition.DependsOnRoles)
            {
                if (!presentRoles.Contains(required))
                {
                    issues.Add(ValidationIssue.Error($"{path}.role",
                        $"{definition.DisplayName} depends on {RoleCatalog.DisplayNameOf(required)}, " +
                        "which is not enabled in this plan."));
                }
            }

            if (server.UseSpotInstance && server.Role is ServerRole.DomainController or ServerRole.AdditionalDomainController)
            {
                issues.Add(ValidationIssue.Warning($"{path}.useSpotInstance",
                    "Spot instances can be evicted at any time; running a domain controller on Spot will break the lab."));
            }

            // ConfigMgr setup reads the local registry for an installed SQL instance and points
            // the site database at its own computer name, so SQL has to be on this VM. A plain
            // Windows Server image fails part-way through the DSC run, long after the VM exists.
            // Warning rather than error: a custom or enclave image may well carry SQL without
            // advertising it in the publisher name, and we cannot tell from here.
            if (server.Role == ServerRole.SccmPrimarySite && !LooksLikeSqlImage(server.Image))
            {
                issues.Add(ValidationIssue.Warning($"{path}.image",
                    "Configuration Manager needs SQL Server on this same VM - setup points the site " +
                    "database at its own computer name. This image does not look like a SQL image, so " +
                    $"use a '{ImageDefaults.SqlPublisher}' image unless SQL is already built into it. " +
                    "Adding a separate SQL Server to the plan does not help; ConfigMgr ignores it."));
            }

            // Said plainly because there is no fix available from inside this tool. The DSC package
            // downloads ConfigMgr from go.microsoft.com/fwlink/?linkid=2093192, which is pinned to
            // the 2403 baseline rather than being evergreen, and 2403 left support on 22 October
            // 2025. Microsoft publishes no anonymous direct-download URL for the current 2509
            // baseline - the only no-volume-licence route is the registration-gated Evaluation
            // Centre - so an unattended build inside a VM cannot reach a supported baseline.
            if (server.Role == ServerRole.SccmPrimarySite)
            {
                issues.Add(ValidationIssue.Warning($"{path}.role",
                    "The shipped DSC package installs the Configuration Manager 2403 baseline, which " +
                    "went out of support on 22 October 2025. There is no anonymous download for the " +
                    "current baseline, so this cannot be fixed automatically. Update the site in the " +
                    "console after the build, or pre-stage supported media, if that matters to you."));
            }

            // Stated because the role reads as a generic "Exchange Server" and the DSC token still
            // says Exchange2019, while InstallExchange.ps1 pins the Exchange Server SE ISO and takes
            // no parameters. SE is in support, so this is no longer a warning about rot - it is
            // about the two things that surprise people in a lab: the licence state and the size of
            // the download, neither of which is visible until the VM has already been built.
            if (server.Role == ServerRole.Exchange)
            {
                issues.Add(ValidationIssue.Warning($"{path}.role",
                    "The shipped DSC package installs Exchange Server SE. With no product key it " +
                    "runs as a 180-day trial, which is fine for a lab. Setup downloads an ISO of " +
                    "about 6 GB inside the VM and takes well over an hour, so give this server " +
                    "time before assuming it has failed."));
            }

            // The download happens inside the VM long after ARM reports success, so a disconnected
            // enclave sees a VM that builds and then never configures, with nothing to explain it.
            // Only warned for a custom cloud: that is the only signal this tool has that the
            // target may be air-gapped, and warning on every commercial plan would be noise.
            // Naming an install media location is the fix, so the warning stands down once one is
            // set - otherwise it would nag about a problem the operator has already solved.
            if (plan.Azure.Cloud == AzureCloud.Custom
                && plan.Artifacts.InstallMediaLocation.Trim().Length == 0
                && RoleCatalog.TryGet(server.Role, out var roleDef)
                && roleDef.InternetDownload is { } download)
            {
                issues.Add(ValidationIssue.Warning($"{path}.role",
                    $"'{roleDef.DisplayName}' downloads {download} while its DSC configuration runs. " +
                    "A disconnected environment cannot reach those, and the VM will deploy " +
                    "successfully and then fail to configure. Set an install media location on the " +
                    "Storage and artifacts page to point this lab at a share, an internal web " +
                    "server or a local path instead, or leave this role out of an air-gapped build."));
            }
        }

        var dcCount = enabled.Count(s => s.Role == ServerRole.DomainController);
        if (dcCount > 1)
        {
            issues.Add(ValidationIssue.Error("servers",
                $"{dcCount} servers are marked as the first Domain Controller. " +
                "Use the Additional Domain Controller role for the others."));
        }

        // An Active Directory forest gets exactly one enterprise root CA. Both halves are errors
        // because each silently builds the wrong lab: two roots, or a CA server that never becomes
        // a CA because the DSC script is told not to install one.
        var caServers = enabled.Count(s => s.Role == ServerRole.CertificateAuthority);
        var placement = plan.Identity.CertificateAuthority;
        if (caServers > 0 && placement != CertificateAuthorityPlacement.DedicatedServer)
        {
            issues.Add(ValidationIssue.Error("identity.certificateAuthority",
                "A Certificate Authority server is in the plan, but the certificate authority is set to " +
                (placement == CertificateAuthorityPlacement.DomainController
                    ? "install on the domain controller. Choose a dedicated server, or remove the CA server."
                    : "none. Choose a dedicated server, or remove the CA server.")));
        }
        else if (caServers == 0 && placement == CertificateAuthorityPlacement.DedicatedServer)
        {
            issues.Add(ValidationIssue.Error("identity.certificateAuthority",
                "The certificate authority is set to run on a dedicated server, but no Certificate " +
                "Authority server is enabled. Add one on the Servers page, or move the CA to the " +
                "domain controller."));
        }

        if (plan.DedicatedHost is { Enabled: true } host)
        {
            if (string.IsNullOrWhiteSpace(host.HostGroupName))
                issues.Add(ValidationIssue.Error("dedicatedHost.hostGroupName", "A host group name is required."));
            if (string.IsNullOrWhiteSpace(host.Sku))
                issues.Add(ValidationIssue.Error("dedicatedHost.sku", "A dedicated host SKU is required."));
        }
    }

    private static void ValidateServerAddress(
        ServerSpec server,
        Cidr? subnet,
        string path,
        Dictionary<string, int> seenIps,
        int index,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(server.PrivateIpAddress))
        {
            issues.Add(ValidationIssue.Error($"{path}.privateIpAddress", "A static private IP address is required."));
            return;
        }

        if (!IPAddress.TryParse(server.PrivateIpAddress, out var ip))
        {
            issues.Add(ValidationIssue.Error($"{path}.privateIpAddress",
                $"'{server.PrivateIpAddress}' is not a valid IPv4 address."));
            return;
        }

        if (seenIps.TryGetValue(server.PrivateIpAddress, out var firstIpIndex))
        {
            issues.Add(ValidationIssue.Error($"{path}.privateIpAddress",
                $"Duplicate IP address '{server.PrivateIpAddress}' (also used by servers[{firstIpIndex}])."));
        }
        else
        {
            seenIps[server.PrivateIpAddress] = index;
        }

        if (subnet is not { } s) return;

        if (!s.Contains(ip))
        {
            issues.Add(ValidationIssue.Error($"{path}.privateIpAddress",
                $"{ip} is outside the workload subnet {s}."));
        }
        else if (s.IsAzureReserved(ip))
        {
            issues.Add(ValidationIssue.Error($"{path}.privateIpAddress",
                $"{ip} is reserved by Azure. The first four and last addresses of {s} cannot be assigned."));
        }
    }

    private static void ValidateAvd(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Avd is not { Enabled: true } avd) return;

        if (string.IsNullOrWhiteSpace(avd.HostPoolName))
            issues.Add(ValidationIssue.Error("avd.hostPoolName", "A host pool name is required."));

        if (string.IsNullOrWhiteSpace(avd.MetadataLocation))
        {
            issues.Add(ValidationIssue.Error("avd.metadataLocation",
                "A metadata region is required. Not every Azure region hosts AVD metadata objects."));
        }

        if (avd.SessionHostCount < 1)
            issues.Add(ValidationIssue.Error("avd.sessionHostCount", "At least one session host is required."));

        if (avd.TokenLifetimeHours is < 1 or > 720)
        {
            issues.Add(ValidationIssue.Error("avd.tokenLifetimeHours",
                "Registration token lifetime must be between 1 and 720 hours."));
        }

        if (!plan.EnabledServers.Any(s => s.Role == ServerRole.DomainController))
        {
            issues.Add(ValidationIssue.Error("avd",
                "Session hosts are domain joined, so a Domain Controller must be enabled in this plan."));
        }
    }

    private static void ValidateMlz(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Mlz is not { Enabled: true } mlz) return;

        // identifier is the single required parameter, and it is woven into every resource name
        // MLZ generates. Upstream constrains it to 1-5 characters; longer values blow the length
        // limit on some of the generated names rather than failing on the parameter itself.
        if (string.IsNullOrWhiteSpace(mlz.Identifier))
        {
            issues.Add(ValidationIssue.Error("mlz.identifier",
                "Mission Landing Zone requires an identifier. It is the only required parameter, " +
                "and it names every resource the deployment creates."));
        }
        else if (!MlzIdentifierPattern.IsMatch(mlz.Identifier))
        {
            issues.Add(ValidationIssue.Error("mlz.identifier",
                $"'{mlz.Identifier}' is not a valid identifier: 1-5 alphanumeric characters only."));
        }

        AllowedValue(issues, "mlz.environmentAbbreviation", mlz.EnvironmentAbbreviation, ["dev", "test", "prod"]);
        AllowedValue(issues, "mlz.firewallSkuTier", mlz.FirewallSkuTier, ["Premium", "Standard"]);
        AllowedValue(issues, "mlz.firewallIntrusionDetectionMode", mlz.FirewallIntrusionDetectionMode, ["Alert", "Deny", "Off"]);
        AllowedValue(issues, "mlz.firewallThreatIntelMode", mlz.FirewallThreatIntelMode, ["Alert", "Deny", "Off"]);
        AllowedValue(issues, "mlz.defenderSkuTier", mlz.DefenderSkuTier, ["Standard", "Free"]);

        if (mlz.DeployPolicy)
        {
            AllowedValue(issues, "mlz.policy", mlz.Policy, ["NISTRev4", "NISTRev5", "IL5", "CMMC"]);
        }

        // Only Premium carries IDPS. Setting a detection mode on Standard is silently ignored by
        // the template, so the operator would believe they had intrusion detection and not have it.
        if (mlz.FirewallSkuTier.Equals("Standard", StringComparison.OrdinalIgnoreCase) &&
            !mlz.FirewallIntrusionDetectionMode.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssue.Warning("mlz.firewallIntrusionDetectionMode",
                "Intrusion detection requires Azure Firewall Premium. On the Standard SKU this " +
                "setting has no effect, and SCCA's VDSS role expects IDPS."));
        }

        if (mlz.DeployDefender && string.IsNullOrWhiteSpace(mlz.EmailSecurityContact))
        {
            issues.Add(ValidationIssue.Warning("mlz.emailSecurityContact",
                "Defender for Cloud is enabled but no security contact email is set, so nobody " +
                "will be notified of alerts."));
        }

        foreach (var (field, value) in new[]
                 {
                     ("mlz.hubSubscriptionId", mlz.HubSubscriptionId),
                     ("mlz.identitySubscriptionId", mlz.IdentitySubscriptionId),
                     ("mlz.operationsSubscriptionId", mlz.OperationsSubscriptionId),
                     ("mlz.sharedServicesSubscriptionId", mlz.SharedServicesSubscriptionId)
                 })
        {
            if (!string.IsNullOrWhiteSpace(value) && !Guid.TryParse(value, out _))
            {
                issues.Add(ValidationIssue.Error(field, $"'{value}' is not a valid subscription GUID."));
            }
        }

        // Deploying MLZ needs Owner, because it assigns roles and creates policy assignments;
        // Contributor is not enough and the failure comes hours in, part-built.
        issues.Add(ValidationIssue.Warning("mlz",
            "Mission Landing Zone deploys at subscription scope and creates its own resource " +
            $"groups - it will not deploy into '{plan.Azure.ResourceGroup}'. It requires Owner on " +
            "the subscription and the 'Encryption At Host' feature registered. Lab servers in this " +
            "plan are not attached to MLZ spokes automatically."));
    }

    private static void AllowedValue(
        List<ValidationIssue> issues,
        string field,
        string value,
        string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssue.Error(field,
                $"'{value}' is not valid. Mission Landing Zone allows: {string.Join(", ", allowed)}."));
        }
    }

    private static void ValidateSaca(DeploymentPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Saca is not { Enabled: true } saca) return;

        // The SACA templates are inherited from f5devcentral/f5-azure-saca, which has had no
        // meaningful commit since March 2023, and they pin marketplace images that no longer
        // resolve. CatalogPreflight cannot catch this: it only inspects plan.Servers, and these
        // image references are hard-coded inside the template rather than bound from the plan.
        // ARM resolves a marketplace image only when the VM is created, so without this the first
        // sign of trouble is a failed deployment.
        issues.Add(ValidationIssue.Warning("saca",
            "The SACA templates pin marketplace images that are no longer available: BIG-IP " +
            "14.1.200000 (F5 supports only 17.1.x, 17.5.x and 21.1.0 in Azure, and publishes only " +
            "the latest to the Marketplace)" +
            (saca.Tier == 3 ? ", and the 3-tier IPS pair uses Ubuntu 18.04-LTS, which is end of life" : "") +
            ". Expect image resolution to fail. Consider Azure Mission Landing Zone " +
            "(github.com/Azure/missionlz) for new SCCA work."));

        if (saca.Tier is not (1 or 3))
            issues.Add(ValidationIssue.Error("saca.tier", "Only 1-tier and 3-tier SACA designs are supported."));

        // Bind() silently drops parameters a template does not declare, so an appliance key that
        // does not match the template's exactly leaves BigIP_VM1_Size at its default of
        // "f5dnst3-bigip0" - a host name used as a VM size. Naming it here turns a late ARM failure
        // into something readable up front.
        var expected = saca.Tier == 3
            ? new[] { "BigIP_VM1", "BigIP_VM2", "BigIP_VM3", "BigIP_VM4", "IPS_FW0", "IPS_FW1", "SB_LB", "NB_LB" }
            : ["BigIP_VM1", "BigIP_VM2", "SB_LB", "NB_LB"];

        foreach (var key in expected.Where(k => !saca.Appliances.ContainsKey(k)))
        {
            issues.Add(ValidationIssue.Warning($"saca.appliances[{key}]",
                $"The {saca.Tier}-tier template declares '{key}', but the plan does not define it. " +
                "Unmatched keys are dropped, and the template's defaults are not deployable."));
        }

        if (string.IsNullOrWhiteSpace(saca.VNetName))
            issues.Add(ValidationIssue.Error("saca.vNetName", "A SACA virtual network name is required."));

        if (string.IsNullOrWhiteSpace(saca.DnsLabel))
            issues.Add(ValidationIssue.Error("saca.dnsLabel", "A SACA DNS label is required."));

        var ranges = new List<(string Key, Cidr Range)>();
        foreach (var (key, value) in saca.Subnets)
        {
            if (!Cidr.TryParse(value.AddressPrefix, out var range))
            {
                issues.Add(ValidationIssue.Error($"saca.subnets[{key}].addressPrefix",
                    $"'{value.AddressPrefix}' is not a valid IPv4 CIDR."));
                continue;
            }

            foreach (var (otherKey, otherRange) in ranges)
            {
                if (range.Overlaps(otherRange))
                {
                    issues.Add(ValidationIssue.Error($"saca.subnets[{key}].addressPrefix",
                        $"{range} overlaps the '{otherKey}' subnet {otherRange}."));
                }
            }

            ranges.Add((key, range));
        }

        foreach (var (applianceKey, appliance) in saca.Appliances)
        {
            foreach (var (nic, address) in appliance.Addresses)
            {
                if (!IPAddress.TryParse(address, out var ip))
                {
                    issues.Add(ValidationIssue.Error($"saca.appliances[{applianceKey}].addresses[{nic}]",
                        $"'{address}' is not a valid IPv4 address."));
                    continue;
                }

                if (ranges.Count > 0 && !ranges.Any(r => r.Range.Contains(ip)))
                {
                    issues.Add(ValidationIssue.Warning($"saca.appliances[{applianceKey}].addresses[{nic}]",
                        $"{ip} does not fall inside any declared SACA subnet."));
                }
            }
        }
    }

    private static bool IsReservedAdminName(string name) =>
        ReservedAdminNames.Contains(name);

    /// <summary>
    /// Whether an image plausibly carries SQL Server. Matches the marketplace SQL publisher, or
    /// an offer that names SQL, which covers the "sql2022-ws2022" style offers and custom images
    /// that say so. Anything else is only *probably* not SQL, which is why the caller warns.
    /// </summary>
    private static bool LooksLikeSqlImage(ImageReferenceSpec? image) =>
        image is not null
        && (string.Equals(image.Publisher, ImageDefaults.SqlPublisher, StringComparison.OrdinalIgnoreCase)
            || image.Offer.Contains("sql", StringComparison.OrdinalIgnoreCase));


    private static readonly HashSet<string> ReservedAdminNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "administrator", "admin", "user", "user1", "test", "user2", "test1", "user3",
        "admin1", "123", "a", "actuser", "adm", "admin2", "aspnet", "backup", "console",
        "david", "guest", "john", "owner", "root", "server", "sql", "support",
        "support_388945a0", "sys", "test2", "test3", "user4", "user5"
    };
}
