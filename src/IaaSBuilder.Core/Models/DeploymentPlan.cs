using System.Text.Json.Serialization;

namespace IaaSBuilder.Core.Models;

/// <summary>
/// The complete, serializable description of an environment to build.
/// <para>
/// This type is the centrepiece of the rewrite. In the legacy script the "plan" existed
/// only as ~200 live WPF controls read directly at deployment time
/// (<c>$WPFserver1IP.Text</c>), which is why saving, loading, validating, testing and
/// scripting all had to be hand-written and all ended up incomplete.
/// </para>
/// <para>
/// Secrets are deliberately absent - see <see cref="DeploymentSecrets"/>.
/// </para>
/// </summary>
public sealed class DeploymentPlan
{
    public const string CurrentSchemaVersion = "2.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Friendly name for this plan, used for log and deployment naming.</summary>
    public string Name { get; set; } = "lab";

    public AzureTarget Azure { get; set; } = new();
    public IdentitySpec Identity { get; set; } = new();
    public NetworkSpec Network { get; set; } = new();
    public ArtifactSpec Artifacts { get; set; } = new();
    public HardeningSpec Hardening { get; set; } = new();
    public BastionSpec Bastion { get; set; } = new();
    public DedicatedHostSpec? DedicatedHost { get; set; }

    /// <summary>
    /// The servers to build. Replaces the fixed set of checkbox-gated, copy-pasted
    /// deployment blocks with an ordinary collection.
    /// </summary>
    public List<ServerSpec> Servers { get; set; } = [];

    /// <summary>SCCA / SACA boundary deployment. Null when not in use.</summary>
    public SacaSpec? Saca { get; set; }

    /// <summary>
    /// Microsoft Mission Landing Zone, the current primary SACA deployment. Null when not in use.
    /// </summary>
    public MlzSpec? Mlz { get; set; }

    /// <summary>Azure Virtual Desktop (formerly WVD). Null when not in use.</summary>
    public AvdSpec? Avd { get; set; }

    /// <summary>All servers that are enabled, in declaration order.</summary>
    [JsonIgnore]
    public IEnumerable<ServerSpec> EnabledServers => Servers.Where(s => s.Enabled);
}

/// <summary>Where the environment is deployed.</summary>
public sealed class AzureTarget
{
    public AzureCloud Cloud { get; set; } = AzureCloud.Public;
    public string SubscriptionId { get; set; } = "";
    public string? TenantId { get; set; }
    public string Location { get; set; } = "";
    public string ResourceGroup { get; set; } = "";

    /// <summary>Tags applied to the resource group. The legacy script applied none.</summary>
    public Dictionary<string, string> Tags { get; set; } = [];
}

/// <summary>Where the lab's enterprise root certificate authority is installed.</summary>
public enum CertificateAuthorityPlacement
{
    /// <summary>No CA at all.</summary>
    None,

    /// <summary>On the first domain controller. What the tool has always done.</summary>
    DomainController,

    /// <summary>On a dedicated Certificate Authority server, which must be added on the Servers page.</summary>
    DedicatedServer
}

/// <summary>Active Directory and local administrator identity settings.</summary>
public sealed class IdentitySpec
{
    public string DomainName { get; set; } = "";
    public string AdminUsername { get; set; } = "xadmin";
    public string AdfsServiceAccountName { get; set; } = "svc.adfs";

    /// <summary>
    /// Where the enterprise root CA goes. Defaults to <see cref="CertificateAuthorityPlacement.DomainController"/>
    /// because that is what every previous version did unconditionally - a saved plan from before
    /// this setting existed must keep building the same lab.
    /// </summary>
    public CertificateAuthorityPlacement CertificateAuthority { get; set; } =
        CertificateAuthorityPlacement.DomainController;

    /// <summary>
    /// Optional Key Vault secret to source the admin password from, so the password
    /// never has to be typed into, or held by, the application at all.
    /// </summary>
    public KeyVaultSecretRef? AdminPasswordSecret { get; set; }

    public string UserPrincipalName =>
        string.IsNullOrWhiteSpace(DomainName) ? AdminUsername : $"{AdminUsername}@{DomainName}";
}

public sealed class KeyVaultSecretRef
{
    public string VaultName { get; set; } = "";
    public string SecretName { get; set; } = "";
}

/// <summary>Virtual network layout.</summary>
public sealed class NetworkSpec
{
    /// <summary>Name prefix applied to generated resources (vnet, nsg, VM names).</summary>
    public string Prefix { get; set; } = "";

    public string? VirtualNetworkName { get; set; }
    public string AddressPrefix { get; set; } = "10.0.0.0/16";
    public string SubnetName { get; set; } = "default";
    public string SubnetPrefix { get; set; } = "10.0.0.0/24";
    public string? NetworkSecurityGroupName { get; set; }

    /// <summary>Create a private endpoint + private DNS zone for the artifact storage account.</summary>
    public bool UsePrivateEndpointForArtifacts { get; set; }

    /// <summary>Create a private DNS zone for <see cref="IdentitySpec.DomainName"/>.</summary>
    public bool CreateDomainPrivateDnsZone { get; set; } = true;

    /// <summary>
    /// Give every VM its own public IP address.
    /// </summary>
    /// <remarks>
    /// Off by default. The built-in policy "Network interfaces should not have public IPs"
    /// (<c>83a86a26-fd1f-447c-b59d-e51f44264114</c>) is assigned with a <c>deny</c> effect in many
    /// enterprise and government tenants, and it rejects the whole VM deployment - not just the
    /// address - so a lab that asks for one gets nothing at all. Azure Bastion reaches the VMs
    /// without a public IP, which is why the legacy SACA template had the block commented out by
    /// hand: somebody hit this and patched around it in one template only.
    /// </remarks>
    public bool AssignPublicIps { get; set; }

    public string EffectiveVirtualNetworkName =>
        string.IsNullOrWhiteSpace(VirtualNetworkName) ? $"{Prefix}-vnet" : VirtualNetworkName;

    public string EffectiveNetworkSecurityGroupName =>
        string.IsNullOrWhiteSpace(NetworkSecurityGroupName) ? $"{Prefix}-nsg" : NetworkSecurityGroupName;
}

/// <summary>Azure Bastion settings.</summary>
public sealed class BastionSpec
{
    public bool Enabled { get; set; }

    /// <summary>Azure requires this subnet to be named AzureBastionSubnet; only the prefix is configurable.</summary>
    public string SubnetPrefix { get; set; } = "10.0.1.0/26";
}

/// <summary>Where DSC / configuration artifacts are staged.</summary>
public sealed class ArtifactSpec
{
    public string StorageAccountName { get; set; } = "";
    public string StorageSku { get; set; } = "Standard_LRS";
    public string ContainerName { get; set; } = "dsc";

    /// <summary>
    /// Resource group holding <see cref="StorageAccountName"/>. Empty means the lab's own resource
    /// group, where the account is created fresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the escape hatch for tenants where the signed-in account cannot be granted
    /// 'Storage Blob Data Contributor' on demand. The staging account is otherwise created in the
    /// lab's resource group under a new random name on every run, so a grant obtained once never
    /// applies to the next deployment - which is why operators kept being asked for the same role
    /// repeatedly.
    /// </para>
    /// <para>
    /// Point this at a long-lived account the operator already has blob data access on and the
    /// grant is made once and reused. When it is set the account must already exist: creating one
    /// silently in a resource group the operator shares with others, because they mistyped a name,
    /// would be worse than refusing.
    /// </para>
    /// </remarks>
    public string StorageResourceGroup { get; set; } = "";

    /// <summary>Path to the DSC package, relative to the content root.</summary>
    public string DscPackagePath { get; set; } = "DSC/Configuration.zip";

    /// <summary>
    /// Fetch the DSC package straight from a public HTTPS URL instead of staging it in Azure
    /// Storage. On by default, because it needs no storage account and no blob data role.
    /// </summary>
    /// <remarks>
    /// The DSC extension only ever needed a URL it could reach from inside the VM; Azure Storage
    /// was simply where the original script happened to put the package, and it is what drags in
    /// the blob data-plane role that Owner and Contributor do not grant. The VM still needs
    /// outbound access to <see cref="PublicPackageUrl"/> - which a staged blob equally required -
    /// so an enclave without it supplies its own location instead.
    /// </remarks>
    public bool UsePublicPackageUrl { get; set; } = true;

    /// <summary>Direct HTTPS link to the DSC package for <see cref="UsePublicPackageUrl"/>.</summary>
    public string PublicPackageUrl { get; set; } = Deployment.PublicPackageSource.DefaultUrl;

    /// <summary>
    /// True only when this tool uploads the package to an Azure storage account.
    /// </summary>
    /// <remarks>
    /// The storage account name, container name, blob data role and Microsoft.Storage provider
    /// registration are all conditional on this one fact. Asking it in a single place stops the
    /// three checks drifting apart and demanding a role for an upload that never happens.
    /// </remarks>
    [JsonIgnore]
    public bool StagesInAzureStorage => !SkipUpload && !UsePublicPackageUrl;

    /// <summary>
    /// Pre-staged artifact URI. Set this for air-gapped runs where the package is
    /// already in place and should not be re-uploaded.
    /// </summary>
    public string? ArtifactsLocationOverride { get; set; }

    /// <summary>
    /// SAS token for <see cref="ArtifactsLocationOverride"/>, with or without the leading '?'.
    /// Leave empty only if the pre-staged location is readable without a credential.
    /// </summary>
    public string? ArtifactsSasTokenOverride { get; set; }

    /// <summary>Skip the upload entirely and rely on <see cref="ArtifactsLocationOverride"/>.</summary>
    public bool SkipUpload { get; set; }

    /// <summary>
    /// Where the VMs fetch third-party installer media from. Empty means the public internet, as
    /// the package has always done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a different problem from <see cref="ArtifactsLocationOverride"/>, which only moves
    /// the DSC package itself. Once that package is running inside the VM it goes on to download
    /// Configuration Manager, the Windows ADK, Exchange, the DISA STIG GPOs and the Microsoft
    /// security baseline from fixed addresses on the public internet. A disconnected enclave can
    /// reach none of them, and the failure is close to invisible: the downloads happen long after
    /// ARM has reported the deployment as succeeded, so the operator sees a VM that builds and
    /// then quietly never finishes configuring.
    /// </para>
    /// <para>
    /// May be an https URL, a UNC share or a local path on the VM, so the same setting covers an
    /// internal web server, a file server and a disk staged into the image. The file names are
    /// fixed rather than individually configurable: one location with a known list of names is
    /// something an operator can prepare and check, whereas ten separate addresses is a form
    /// nobody fills in correctly.
    /// </para>
    /// </remarks>
    public string InstallMediaLocation { get; set; } = "";

    /// <summary>
    /// Allow a VM to fall back to the Microsoft download when a file is missing from
    /// <see cref="InstallMediaLocation"/>.
    /// </summary>
    /// <remarks>
    /// Off by default. Somebody who named a location has usually done so because the internet is
    /// not reachable, and in that case falling back silently replaces a precise "stage this file"
    /// message with an hour-long hang that never mentions media at all. Turning it on suits a
    /// connected network where the location is only a partial mirror holding the larger files.
    /// </remarks>
    public bool InstallMediaFallbackToInternet { get; set; }
}

/// <summary>Security baseline toggles.</summary>
public sealed class HardeningSpec
{
    /// <summary>Apply DISA STIG configuration.</summary>
    public bool ApplyStig { get; set; }

    /// <summary>Apply the Microsoft Security Baseline.</summary>
    public bool ApplyMicrosoftBaseline { get; set; }
}

/// <summary>Azure Dedicated Host placement.</summary>
public sealed class DedicatedHostSpec
{
    public bool Enabled { get; set; }
    public string HostGroupName { get; set; } = "";
    public string Sku { get; set; } = "";

    /// <summary>Resolved host resource id; populated at deployment time when empty.</summary>
    public string? HostId { get; set; }
}

/// <summary>A single VM (or a small identical set of VMs) to deploy.</summary>
public sealed class ServerSpec
{
    public bool Enabled { get; set; } = true;
    public ServerRole Role { get; set; }

    /// <summary>Computer name. Must be <= 15 characters for a domain-joined Windows host.</summary>
    public string Name { get; set; } = "";

    /// <summary>Static private IPv4 address within <see cref="NetworkSpec.SubnetPrefix"/>.</summary>
    public string PrivateIpAddress { get; set; } = "";

    public string VmSize { get; set; } = "Standard_D2s_v5";
    public string DiskType { get; set; } = "Premium_LRS";

    public ImageReferenceSpec Image { get; set; } = new();

    /// <summary>Deploy as an Azure Spot VM.</summary>
    public bool UseSpotInstance { get; set; }

    /// <summary>
    /// SharePoint farm version, only meaningful for <see cref="ServerRole.SharePoint"/>.
    /// </summary>
    public string? SharePointVersion { get; set; }

    /// <summary>
    /// Explicit extra template parameters, for cases the model does not cover.
    /// Values here win over generated ones.
    /// </summary>
    public Dictionary<string, object?> ExtraParameters { get; set; } = [];

    /// <summary>Stable identifier used by the dependency graph.</summary>
    [JsonIgnore]
    public string StepId => $"vm:{Name}";
}

/// <summary>Marketplace image coordinates.</summary>
public sealed class ImageReferenceSpec
{
    public string Publisher { get; set; } = "MicrosoftWindowsServer";
    public string Offer { get; set; } = "WindowsServer";
    public string Sku { get; set; } = "2022-datacenter-azure-edition";

    public override string ToString() => $"{Publisher}:{Offer}:{Sku}";
}

/// <summary>Azure Virtual Desktop host pool settings (the artist formerly known as WVD).</summary>
public sealed class AvdSpec
{
    public bool Enabled { get; set; }
    public string HostPoolName { get; set; } = "";

    /// <summary>Region that holds the AVD metadata objects; a subset of Azure regions.</summary>
    public string MetadataLocation { get; set; } = "";

    public string VmSize { get; set; } = "Standard_D4s_v5";
    public string DiskType { get; set; } = "Premium_LRS";
    public int SessionHostCount { get; set; } = 2;
    public ImageReferenceSpec Image { get; set; } = ImageDefaults.AvdSessionHost;
    public string HostPoolType { get; set; } = "Pooled";
    public string LoadBalancerType { get; set; } = "BreadthFirst";

    /// <summary>
    /// Registration token lifetime. The legacy script hard-coded "now + 1 day"
    /// at script scope, so it went stale on long-running sessions.
    /// </summary>
    public int TokenLifetimeHours { get; set; } = 8;
}

/// <summary>
/// Microsoft Mission Landing Zone settings.
/// </summary>
/// <remarks>
/// MLZ is deployed from the vendored, unmodified <c>Templates/MLZ/mlz.json</c> and is
/// <b>subscription-scoped</b>: it creates its own resource groups, one per tier, so it does not
/// deploy into <see cref="AzureTarget.ResourceGroup"/> like everything else here.
/// <para>
/// Only the settings worth an operator's attention are modelled. MLZ declares over a hundred
/// parameters and requires exactly one, <c>identifier</c>; the rest of the defaults are upstream's
/// and are deliberately left alone so a template refresh picks up their current recommendations.
/// </para>
/// <para>
/// The defaults below are the template's own, restated so the UI can show what will happen rather
/// than leaving fields blank. <c>DeployDefender</c> is <c>true</c> upstream even though the docs
/// claim otherwise - the template is the authority.
/// </para>
/// </remarks>
public sealed class MlzSpec
{
    public bool Enabled { get; set; }

    /// <summary>1-5 alphanumeric characters, woven into every resource name MLZ creates.</summary>
    public string Identifier { get; set; } = "";

    /// <summary>dev, test or prod.</summary>
    public string EnvironmentAbbreviation { get; set; } = "dev";

    /// <summary>Blank means "the plan's subscription", which is the single-subscription layout.</summary>
    public string? HubSubscriptionId { get; set; }
    public string? IdentitySubscriptionId { get; set; }
    public string? OperationsSubscriptionId { get; set; }
    public string? SharedServicesSubscriptionId { get; set; }

    /// <summary>The identity tier is opt-in upstream; hub, operations and shared services are always built.</summary>
    public bool DeployIdentity { get; set; }

    public bool DeployBastion { get; set; } = true;

    /// <summary>Premium or Standard. Premium carries the IDPS that SCCA's VDSS role expects.</summary>
    public string FirewallSkuTier { get; set; } = "Premium";

    /// <summary>Alert, Deny or Off.</summary>
    public string FirewallIntrusionDetectionMode { get; set; } = "Alert";

    /// <summary>Alert, Deny or Off.</summary>
    public string FirewallThreatIntelMode { get; set; } = "Alert";

    public bool DeployDefender { get; set; } = true;

    /// <summary>Standard or Free.</summary>
    public string DefenderSkuTier { get; set; } = "Free";

    /// <summary>Where Defender sends alerts. Only meaningful when <see cref="DeployDefender"/> is set.</summary>
    public string EmailSecurityContact { get; set; } = "";

    public bool DeploySentinel { get; set; }

    public bool DeployPolicy { get; set; }

    /// <summary>NISTRev4, NISTRev5, IL5 or CMMC. Only applied when <see cref="DeployPolicy"/> is set.</summary>
    public string Policy { get; set; } = "NISTRev4";
}

/// <summary>Secure Cloud Computing Architecture / SACA boundary settings.</summary>
public sealed class SacaSpec
{
    public bool Enabled { get; set; }

    /// <summary>1 or 3.</summary>
    public int Tier { get; set; } = 1;

    public string VNetName { get; set; } = "";
    public string DnsLabel { get; set; } = "";

    /// <summary>Named subnets, e.g. "Management" -> 10.90.1.0/24.</summary>
    public Dictionary<string, SacaSubnet> Subnets { get; set; } = [];

    /// <summary>Appliances (BIG-IPs, IPS pairs, jump boxes) keyed by logical name.</summary>
    public Dictionary<string, SacaAppliance> Appliances { get; set; } = [];

    /// <summary>Shared load balancer / self-IP addresses keyed by logical name.</summary>
    public Dictionary<string, string> SharedAddresses { get; set; } = [];
}

public sealed class SacaSubnet
{
    public string Name { get; set; } = "";
    public string AddressPrefix { get; set; } = "";
}

/// <summary>
/// A SACA network appliance. The legacy form had ~45 individually named text boxes
/// (SACA_BIGIP1Ext1Pri_IP, SACA_BIGIP2INTNSec_IP, ...); this collapses them to data.
/// </summary>
public sealed class SacaAppliance
{
    public string Name { get; set; } = "";
    public string VmSize { get; set; } = "";

    /// <summary>NIC name -> IP address, e.g. "Ext1Pri" -> 10.90.2.11.</summary>
    public Dictionary<string, string> Addresses { get; set; } = [];
}
