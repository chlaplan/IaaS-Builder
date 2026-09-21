namespace IaaSBuilder.Core.Catalog;

/// <summary>
/// One VM size as Azure reports it for a region.
/// </summary>
/// <param name="PremiumIo">
/// Whether the size can attach Premium SSD disks. <see langword="null"/> means unknown, which is
/// what a snapshot captured before this field existed deserializes to - so it must never be
/// treated as "no", or an offline enclave running an older snapshot would have every size
/// reported as incapable.
/// </param>
public sealed record VmSizeInfo(
    string Name,
    int Cores,
    int MemoryMb,
    int MaxDataDisks,
    bool? PremiumIo = null)
{
    public string Family => Name.Split('_').Length > 1 ? Name.Split('_')[1] : Name;

    /// <summary>
    /// True when this size is known <em>not</em> to support Premium SSD. Deliberately not the
    /// negation of <see cref="PremiumIo"/>: unknown has to behave like "allow", because the
    /// alternative is blocking a deployment on missing metadata.
    /// </summary>
    public bool KnownToRejectPremium => PremiumIo == false;

    public int MemoryGb => MemoryMb / 1024;

    /// <summary>
    /// The size series - "D", "B", "NC" and so on - used to group a region's several hundred sizes
    /// into a dropdown someone can actually read. Taken from the leading letters of the size token,
    /// so <c>Standard_D2s_v5</c>, <c>Standard_D16as_v5</c> and <c>Standard_D2_v2</c> all land in "D".
    /// </summary>
    /// <remarks>
    /// Returns an empty string for anything that does not parse, and callers group those under a
    /// catch-all rather than dropping them. A size Azure offers must always be selectable, whatever
    /// its name looks like.
    /// </remarks>
    public string Series
    {
        get
        {
            var parts = Name.Split('_');
            var token = parts.Length > 1 ? parts[1] : parts[0];

            var letters = token.TakeWhile(char.IsLetter).ToArray();
            return new string(letters).ToUpperInvariant();
        }
    }
}

public sealed record ImageSkuInfo(string Publisher, string Offer, string Sku)
{
    public string Key => $"{Publisher}/{Offer}";
}

public sealed record LocationInfo(string Name, string DisplayName, IReadOnlyList<string> Providers)
{
    public bool SupportsAvd =>
        Providers.Contains("Microsoft.DesktopVirtualization", StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A snapshot of the Azure metadata the UI needs to populate its dropdowns.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes offline operation possible. Every dropdown in the legacy tool was
/// filled by a live call - <c>Get-AzLocation</c>, <c>Get-AzVMSize</c>,
/// <c>Get-AzVMImageSku</c>, <c>Get-AzComputeResourceSku</c> - fired synchronously from a
/// <c>SelectionChanged</c> handler. With no connection the form came up empty and unusable,
/// and even when connected, changing region froze the window for several seconds.
/// </para>
/// <para>
/// Now the catalog is refreshed when a connection is available, written to disk, and read
/// from disk thereafter. An air-gapped enclave ships with the snapshot file.
/// </para>
/// </remarks>
public sealed class ResourceCatalog
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Cloud { get; set; } = "Public";

    public List<LocationInfo> Locations { get; set; } = [];

    /// <summary>VM sizes keyed by region name.</summary>
    public Dictionary<string, List<VmSizeInfo>> VmSizesByLocation { get; set; } = [];

    /// <summary>Image SKUs keyed by "region|publisher/offer".</summary>
    public Dictionary<string, List<string>> ImageSkus { get; set; } = [];

    /// <summary>
    /// Every image publisher the region offers, keyed by region name.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ImageSkus"/> because it is a complete list, not a sample. The SKU
    /// map only ever holds the handful of publisher/offer pairs this tool ships roles for -
    /// deriving the publisher dropdown from it meant offering four publishers where a region
    /// stocks several hundred, which is what made the dropdowns look filtered. One call per
    /// region fills this, so it is cheap enough to collect on every refresh and it travels in the
    /// offline snapshot like everything else.
    /// </remarks>
    public Dictionary<string, List<string>> ImagePublishersByLocation { get; set; } = [];

    /// <summary>Dedicated host SKUs keyed by region name.</summary>
    public Dictionary<string, List<string>> DedicatedHostSkusByLocation { get; set; } = [];

    public TimeSpan Age => DateTimeOffset.UtcNow - CapturedUtc;

    public IReadOnlyList<VmSizeInfo> GetVmSizes(string location) =>
        VmSizesByLocation.TryGetValue(location, out var sizes) ? sizes : [];

    public IReadOnlyList<string> GetImageSkus(string location, string publisher, string offer) =>
        ImageSkus.TryGetValue(BuildImageKey(location, publisher, offer), out var skus) ? skus : [];

    public IReadOnlyList<string> GetDedicatedHostSkus(string location) =>
        DedicatedHostSkusByLocation.TryGetValue(location, out var skus) ? skus : [];

    public IReadOnlyList<string> GetImagePublishers(string location) =>
        ImagePublishersByLocation.TryGetValue(location, out var publishers) ? publishers : [];

    public IEnumerable<LocationInfo> GetAvdLocations() => Locations.Where(l => l.SupportsAvd);

    public void SetImageSkus(string location, string publisher, string offer, IEnumerable<string> skus) =>
        ImageSkus[BuildImageKey(location, publisher, offer)] = skus.ToList();

    public static string BuildImageKey(string location, string publisher, string offer) =>
        $"{location}|{publisher}/{offer}";

    /// <summary>True when the snapshot is old enough that it is worth refreshing.</summary>
    public bool IsStale(TimeSpan maxAge) => Age > maxAge;
}
