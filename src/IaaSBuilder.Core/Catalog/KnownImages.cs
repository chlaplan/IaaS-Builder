namespace IaaSBuilder.Core.Catalog;

/// <summary>
/// The marketplace images this tool knows about, as a publisher -> offer -> SKU tree.
/// </summary>
/// <remarks>
/// <para>
/// This serves two callers that previously kept separate lists. <c>AzureCatalogSource</c> uses
/// it to decide which publisher/offer pairs are worth pre-caching SKUs for, and the UI uses it
/// to populate the image dropdowns when the catalog has nothing for the selected region.
/// </para>
/// <para>
/// Keeping one list matters because the UI only offers publishers and offers it can also offer
/// SKUs for. When the two lists were separate, the publisher dropdown was populated from a
/// hard-coded array while the offer and SKU dropdowns had no fallback at all, so picking a
/// publisher led straight to two empty dropdowns and the operator had to type exact strings
/// from memory - which is what the dropdowns existed to prevent.
/// </para>
/// <para>
/// These are starting points, not a guarantee: image availability is per-region and per-cloud,
/// and sovereign clouds lag the commercial catalog. Every image field still accepts free text,
/// and a live catalog refresh replaces these values with what the region actually offers.
/// </para>
/// </remarks>
public static class KnownImages
{
    /// <summary>A marketplace offer and the SKUs worth listing under it.</summary>
    public sealed record KnownOffer(string Name, IReadOnlyList<string> Skus);

    /// <summary>A publisher and its offers.</summary>
    public sealed record KnownPublisher(string Name, IReadOnlyList<KnownOffer> Offers);

    /// <summary>
    /// Ordered so the most commonly used publisher comes first; the dropdowns preserve this
    /// order rather than sorting alphabetically, which would put SQL Server above Windows Server.
    /// </summary>
    private static readonly KnownPublisher[] All =
    [
        new("MicrosoftWindowsServer",
        [
            new("WindowsServer",
            [
                "2025-datacenter-azure-edition",
                "2025-datacenter-g2",
                "2022-datacenter-azure-edition",
                "2022-datacenter-g2",
                "2019-datacenter-gensecond",
                "2016-datacenter-gensecond"
            ])
        ]),

        new("MicrosoftWindowsDesktop",
        [
            new("Windows-11",
            [
                "win11-24h2-ent",
                "win11-24h2-avd",
                "win11-23h2-ent",
                "win11-23h2-avd"
            ]),
            new("Windows-10",
            [
                "win10-22h2-ent-g2",
                "win10-22h2-avd-g2"
            ]),
            // Multi-session images with Microsoft 365 Apps pre-installed, for AVD session hosts.
            new("office-365",
            [
                "win11-24h2-avd-m365",
                "win11-23h2-avd-m365",
                "win10-22h2-avd-m365-g2"
            ])
        ]),

        new("MicrosoftSQLServer",
        [
            new("sql2022-ws2022", SqlSkus),
            new("sql2019-ws2022", SqlSkus)
        ]),

        new("MicrosoftSharePoint",
        [
            new("MicrosoftSharePointServer", ["sp2019", "sp2016"])
        ])
    ];

    /// <summary>
    /// SQL edition SKUs, identical across the supported offers. "sqldev" is the developer
    /// edition, which is free but licensed for non-production use - the usual choice for a lab.
    /// </summary>
    private static IReadOnlyList<string> SqlSkus =>
        ["standard", "enterprise", "sqldev", "web", "express"];

    /// <summary>Every known publisher, in display order.</summary>
    public static IReadOnlyList<string> Publishers { get; } =
        All.Select(p => p.Name).ToArray();

    /// <summary>Offers for a publisher, or an empty list if the publisher is not known.</summary>
    public static IReadOnlyList<string> OffersFor(string? publisher) =>
        Find(publisher)?.Offers.Select(o => o.Name).ToArray() ?? [];

    /// <summary>SKUs for a publisher/offer pair, or an empty list if the pair is not known.</summary>
    public static IReadOnlyList<string> SkusFor(string? publisher, string? offer)
    {
        if (string.IsNullOrWhiteSpace(offer))
        {
            return [];
        }

        var match = Find(publisher)?.Offers
            .FirstOrDefault(o => string.Equals(o.Name, offer, StringComparison.OrdinalIgnoreCase));

        return match?.Skus ?? [];
    }

    /// <summary>
    /// Every publisher/offer pair, for the catalog refresh to pre-cache SKUs against.
    /// </summary>
    public static IReadOnlyList<(string Publisher, string Offer)> Pairs { get; } =
        All.SelectMany(p => p.Offers.Select(o => (p.Name, o.Name))).ToArray();

    private static KnownPublisher? Find(string? publisher) =>
        string.IsNullOrWhiteSpace(publisher)
            ? null
            : All.FirstOrDefault(p =>
                string.Equals(p.Name, publisher, StringComparison.OrdinalIgnoreCase));
}
