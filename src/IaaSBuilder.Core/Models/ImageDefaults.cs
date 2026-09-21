namespace IaaSBuilder.Core.Models;

/// <summary>
/// Default marketplace images, in one place.
/// </summary>
/// <remarks>
/// <para>
/// These are the values a new plan starts with. A wrong or delisted SKU is an expensive
/// mistake: ARM only resolves a marketplace image when the VM is actually created, so the
/// failure appears several minutes into a deployment, after the network and often a domain
/// controller already exist. <c>CatalogPreflight</c> catches it earlier when a catalog
/// snapshot is present, but a fresh install in an enclave may not have one.
/// </para>
/// <para>
/// Client images age out on a fixed schedule and must be reviewed. Windows 11 23H2
/// Enterprise/Education reached end of servicing on 2026-11-10 and was replaced here;
/// Microsoft does in practice remove images from the Marketplace at end of servicing
/// (22H2 and the 23H2 Pro family are already gone from the catalog).
/// </para>
/// <para>
/// The server image deliberately lags the newest release. Exchange 2019 and SharePoint 2019
/// - both shipped as roles by this tool - are not supported on Windows Server 2025, so
/// moving the default forward would break those DSC configurations.
/// </para>
/// </remarks>
public static class ImageDefaults
{
    /// <summary>
    /// Windows client version used by the Workstation role and the AVD session hosts.
    /// Kept as one constant so the two cannot drift apart.
    /// </summary>
    /// <remarks>
    /// 24H2 rather than the newest build: this tool targets sovereign and air-gapped clouds,
    /// where image versions lag the commercial catalog, and 24H2 is supported until 2027-10-12.
    /// </remarks>
    public const string WindowsClientVersion = "24h2";

    /// <summary>End of servicing for <see cref="WindowsClientVersion"/>, Enterprise editions.</summary>
    public static readonly DateOnly WindowsClientEndOfServicing = new(2027, 10, 12);

    public const string WindowsServerSku = "2022-datacenter-azure-edition";

    /// <summary>Single-session Windows client, for the Workstation role.</summary>
    public static ImageReferenceSpec WindowsClient => new()
    {
        Publisher = "MicrosoftWindowsDesktop",
        Offer = "Windows-11",
        Sku = $"win11-{WindowsClientVersion}-ent"
    };

    /// <summary>Multi-session Windows client with Microsoft 365 Apps, for AVD session hosts.</summary>
    public static ImageReferenceSpec AvdSessionHost => new()
    {
        Publisher = "MicrosoftWindowsDesktop",
        Offer = "office-365",
        Sku = $"win11-{WindowsClientVersion}-avd-m365"
    };

    public static ImageReferenceSpec WindowsServer => new()
    {
        Publisher = "MicrosoftWindowsServer",
        Offer = "WindowsServer",
        Sku = WindowsServerSku
    };

    /// <summary>
    /// Windows Server with SQL Server pre-installed.
    /// </summary>
    /// <remarks>
    /// Used by the SQL role and - less obviously - by the Configuration Manager primary site.
    /// <c>InstallAndUpdateSCCM.ps1</c> reads the local registry for
    /// <c>InstalledInstances[0]</c> and then points the site at
    /// <c>"$env:computername.$DomainFullName"</c>, so ConfigMgr requires SQL on its *own* VM.
    /// The <c>SQLName</c> parameter that gets passed to the configuration is accepted and never
    /// used, which makes the plumbing look like it supports a remote SQL server when it does not.
    /// </remarks>
    public static ImageReferenceSpec SqlServer => new()
    {
        Publisher = "MicrosoftSQLServer",
        Offer = "sql2019-ws2022",
        Sku = "standard"
    };

    /// <summary>
    /// Publisher of the marketplace SQL images, used to recognise an image that carries SQL.
    /// </summary>
    public const string SqlPublisher = "MicrosoftSQLServer";
}
