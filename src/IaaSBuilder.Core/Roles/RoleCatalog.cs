using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Roles;

/// <summary>
/// Everything the deployment engine needs to know about a server role.
/// </summary>
/// <param name="Role">The modelled role.</param>
/// <param name="DisplayName">Label for the UI.</param>
/// <param name="DscToken">
/// Value passed as the ARM <c>role</c> parameter. The template builds its DSC extension
/// settings as <c>concat(role, 'Configuration.ps1\Configuration')</c>, so this token must
/// match a script inside DSC/Configuration.zip exactly.
/// </param>
/// <param name="DefaultImage">Image used when the plan does not override it.</param>
/// <param name="DefaultVmSize">Size used when the plan does not override it.</param>
/// <param name="DependsOnRoles">
/// Roles that must finish deploying first. This is the data that replaces
/// <c>Start-Sleep -Seconds 660</c>.
/// </param>
/// <param name="RequiresDomain">Whether the role needs a domain to exist.</param>
/// <param name="NameSuffix">Default computer-name suffix appended to the prefix.</param>
/// <param name="Tier">
/// Administrative trust tier. Deliberately has no default: adding a role should force a decision
/// about how much damage it can do, rather than silently inheriting one.
/// </param>
/// <param name="InternetDownload">
/// What the DSC configuration downloads from the public internet while it runs, or null when it
/// installs only from the image and the staged DSC package. This is recorded per role rather
/// than checked at deployment time because the download happens inside the VM, minutes after ARM
/// has already reported success - in a disconnected enclave the VM builds, the extension times
/// out, and nothing explains why.
/// </param>
public sealed record RoleDefinition(
    ServerRole Role,
    string DisplayName,
    string DscToken,
    ImageReferenceSpec DefaultImage,
    string DefaultVmSize,
    IReadOnlyList<ServerRole> DependsOnRoles,
    bool RequiresDomain,
    string NameSuffix,
    TrustTier Tier,
    string? InternetDownload = null,
    bool SingleInstance = false);

/// <summary>
/// The role table.
/// </summary>
/// <remarks>
/// This single table replaces roughly 700 lines of the legacy script: 25 near-identical
/// <c>New-AzResourceGroupDeployment</c> blocks that differed only in which controls they
/// read and which literal role string they passed. Adding a role is now one row plus a DSC
/// script, not another 30-line copy-paste.
/// </remarks>
public static class RoleCatalog
{
    private static readonly ImageReferenceSpec WindowsServer = ImageDefaults.WindowsServer;

    private static ImageReferenceSpec Server(string sku) => new()
    {
        Publisher = WindowsServer.Publisher,
        Offer = WindowsServer.Offer,
        Sku = sku
    };

    private static readonly Dictionary<ServerRole, RoleDefinition> Definitions =
        new RoleDefinition[]
        {
            new(ServerRole.DomainController, "Domain Controller", "DC",
                Server("2022-datacenter-azure-edition"), "Standard_D2s_v5",
                [], RequiresDomain: false, NameSuffix: "dc01",  Tier: TrustTier.Tier0,
                SingleInstance: true),

            new(ServerRole.AdditionalDomainController, "Additional Domain Controller", "AddDC",
                Server("2022-datacenter-azure-edition"), "Standard_D2s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "dc02",  Tier: TrustTier.Tier0),

            // Enterprise root CA on its own server. Only reachable when
            // Identity.CertificateAuthority is DedicatedServer - otherwise the DC installs one,
            // and a forest must never end up with two enterprise roots.
            new(ServerRole.CertificateAuthority, "Certificate Authority", "CA",
                Server("2022-datacenter-azure-edition"), "Standard_D2s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "ca01", Tier: TrustTier.Tier0,
                SingleInstance: true),

            new(ServerRole.Adfs, "AD FS", "ADFS",
                Server("2022-datacenter-azure-edition"), "Standard_D2s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "adfs01", Tier: TrustTier.Tier0),

            // Named "Exchange Server" rather than a version because the DSC token says Exchange2019
            // and InstallExchange.ps1 takes no parameters - the version is whichever ISO that script
            // is pinned to. It now installs Exchange Server SE, the only release still in support:
            // 2016 and 2019 both left support on 14 October 2025, and 2016 was never supported on
            // Windows Server 2022 at all, which is the image this role deploys.
            new(ServerRole.Exchange, "Exchange Server", "Exchange2019",
                Server("2022-datacenter-azure-edition"), "Standard_D4s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "ex01",  Tier: TrustTier.Tier1,
                InternetDownload: "the Exchange Server SE ISO (about 6 GB), the UCMA runtime, the "
                    + "2012 and 2013 Visual C++ redistributables and the IIS URL Rewrite module "
                    + "from download.microsoft.com"),

            new(ServerRole.SharePoint, "SharePoint Server", "SP",
                new ImageReferenceSpec
                {
                    Publisher = "MicrosoftSharePoint",
                    Offer = "MicrosoftSharePointServer",
                    Sku = "sp2019"
                },
                "Standard_D4s_v5",
                [ServerRole.DomainController, ServerRole.Sql], RequiresDomain: true, NameSuffix: "sp01",  Tier: TrustTier.Tier1),

            new(ServerRole.Sql, "SQL Server", "SQL",
                ImageDefaults.SqlServer,
                "Standard_D4s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "sql01", Tier: TrustTier.Tier1),

            // Deliberately does NOT depend on the Sql role. ConfigMgr setup points the site at
            // its own computer name and reads the local registry for the SQL instance, so it
            // needs SQL on this VM - a separate SQL server would be built and then ignored.
            new(ServerRole.SccmPrimarySite, "Configuration Manager Primary Site", "PS",
                ImageDefaults.SqlServer, "Standard_D4s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "ps01",  Tier: TrustTier.Tier1,
                InternetDownload: "the Windows ADK, the ADK Windows PE add-on and the Configuration "
                    + "Manager installer from go.microsoft.com"),

            new(ServerRole.SccmDistributionPoint, "Configuration Manager DP/MP", "DPMP",
                Server("2022-datacenter-azure-edition"), "Standard_D2s_v5",
                [ServerRole.DomainController, ServerRole.SccmPrimarySite], RequiresDomain: true, NameSuffix: "dp01",  Tier: TrustTier.Tier1),

            new(ServerRole.Workstation, "Workstation", "JoinDomain",
                ImageDefaults.WindowsClient,
                "Standard_D2s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "ws01",  Tier: TrustTier.Tier2),

            new(ServerRole.MemberServer, "Member Server", "JoinDomain",
                Server("2022-datacenter-azure-edition"), "Standard_D2s_v5",
                [ServerRole.DomainController], RequiresDomain: true, NameSuffix: "srv01", Tier: TrustTier.Tier1)
        }.ToDictionary(d => d.Role);

    public static IReadOnlyCollection<RoleDefinition> All => Definitions.Values;

    public static RoleDefinition Get(ServerRole role) =>
        Definitions.TryGetValue(role, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(role), role, "No role definition registered.");

    /// <summary>
    /// Non-throwing lookup, for plans loaded from disk that may name a role which is no longer
    /// supported by the shipped DSC package.
    /// </summary>
    public static bool TryGet(ServerRole role, out RoleDefinition definition) =>
        Definitions.TryGetValue(role, out definition!);

    /// <summary>Safe for display: never throws, so a retired role cannot break a page render.</summary>
    public static string DisplayNameOf(ServerRole role) =>
        Definitions.TryGetValue(role, out var definition) ? definition.DisplayName : $"{role} (unsupported)";

    /// <summary>Creates a server pre-populated from the role defaults.</summary>
    public static ServerSpec CreateDefault(ServerRole role, string prefix)
    {
        var definition = Get(role);
        return new ServerSpec
        {
            Role = role,
            Name = $"{prefix}{definition.NameSuffix}",
            VmSize = definition.DefaultVmSize,
            Image = new ImageReferenceSpec
            {
                Publisher = definition.DefaultImage.Publisher,
                Offer = definition.DefaultImage.Offer,
                Sku = definition.DefaultImage.Sku
            }
        };
    }
}
