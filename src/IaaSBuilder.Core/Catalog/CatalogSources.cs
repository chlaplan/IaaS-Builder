using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Resources;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Catalog;

/// <summary>Supplies the resource catalog, online or offline.</summary>
public interface ICatalogSource
{
    Task<ResourceCatalog> GetAsync(CancellationToken ct = default);
}

/// <summary>Reads and writes catalog snapshots on disk.</summary>
public sealed class CatalogFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CatalogFileStore(string path) => Path = System.IO.Path.GetFullPath(path);

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public async Task<ResourceCatalog?> TryLoadAsync(CancellationToken ct = default)
    {
        if (!Exists) return null;

        try
        {
            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<ResourceCatalog>(stream, Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt cache must never stop the app starting; it just means no dropdown data.
            return null;
        }
    }

    public async Task SaveAsync(ResourceCatalog catalog, CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-then-rename so an interrupted refresh cannot leave a truncated snapshot.
        var temporary = Path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, Options, ct);
        }

        File.Move(temporary, Path, overwrite: true);
    }
}

/// <summary>Reads the catalog from a snapshot file only. Used when air-gapped.</summary>
public sealed class OfflineCatalogSource : ICatalogSource
{
    private readonly CatalogFileStore _store;

    public OfflineCatalogSource(CatalogFileStore store) => _store = store;

    public async Task<ResourceCatalog> GetAsync(CancellationToken ct = default) =>
        await _store.TryLoadAsync(ct) ?? new ResourceCatalog();
}

/// <summary>
/// Queries Azure for the catalog and writes a snapshot for later offline use.
/// </summary>
public sealed class AzureCatalogSource : ICatalogSource
{
    /// <summary>
    /// Publisher/offer pairs worth pre-caching for the built-in roles. Shared with the UI's
    /// fallback dropdowns via <see cref="KnownImages"/> so the two cannot drift: a pair the
    /// operator can select but the refresh never caches would show an empty SKU list.
    /// </summary>
    private static IReadOnlyList<(string Publisher, string Offer)> TrackedImages =>
        KnownImages.Pairs;

    private readonly ArmClient _client;
    private readonly string _subscriptionId;
    private readonly AzureCloud _cloud;
    private readonly CatalogFileStore? _store;
    private readonly HashSet<string>? _imageSkuLocations;

    /// <param name="imageSkuLocations">
    /// Regions to fetch image SKUs for, or null for every region. This is the difference between a
    /// fast refresh and a slow one: image SKUs are one call per region per publisher/offer, so all
    /// regions costs roughly 70 x 7 = ~490 sequential round trips (minutes), while the single
    /// region being deployed to costs 7. The full sweep is what the offline snapshot needs; the UI
    /// only ever displays the selected region.
    /// </param>
    public AzureCatalogSource(
        TokenCredential credential,
        AzureCloud cloud,
        string subscriptionId,
        CatalogFileStore? store = null,
        IEnumerable<string>? imageSkuLocations = null)
    {
        _cloud = cloud;
        _subscriptionId = subscriptionId;
        _store = store;
        _imageSkuLocations = imageSkuLocations is null
            ? null
            : new HashSet<string>(
                imageSkuLocations.Where(l => !string.IsNullOrWhiteSpace(l)),
                StringComparer.OrdinalIgnoreCase);
        _client = new ArmClient(credential, subscriptionId, new ArmClientOptions
        {
            Environment = Azure.AzureCloudEndpoints.GetArmEnvironment(cloud)
        });
    }

    public async Task<ResourceCatalog> GetAsync(CancellationToken ct = default)
    {
        var catalog = new ResourceCatalog
        {
            Cloud = _cloud.ToString(),
            CapturedUtc = DateTimeOffset.UtcNow
        };

        var subscription = _client.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{_subscriptionId}"));

        await foreach (var location in subscription.GetLocationsAsync(cancellationToken: ct))
        {
            catalog.Locations.Add(new LocationInfo(
                location.Name,
                location.DisplayName ?? location.Name,
                []));
        }

        // Compute SKUs come back for the whole subscription in one pass, so index them
        // by region here rather than making a call per region as the legacy script did.
        await foreach (var sku in subscription.GetComputeResourceSkusAsync(cancellationToken: ct))
        {
            if (sku.Locations is null) continue;

            foreach (var location in sku.Locations)
            {
                if (string.Equals(sku.ResourceType, "virtualMachines", StringComparison.OrdinalIgnoreCase))
                {
                    var sizes = GetOrAdd(catalog.VmSizesByLocation, location);
                    sizes.Add(new VmSizeInfo(
                        sku.Name,
                        GetCapabilityInt(sku, "vCPUs"),
                        GetCapabilityInt(sku, "MemoryGB") * 1024,
                        GetCapabilityInt(sku, "MaxDataDiskCount"),
                        GetCapabilityBool(sku, "PremiumIO")));
                }
                else if (string.Equals(sku.ResourceType, "hostGroups/hosts", StringComparison.OrdinalIgnoreCase))
                {
                    GetOrAdd(catalog.DedicatedHostSkusByLocation, location).Add(sku.Name);
                }
            }
        }

        foreach (var sizes in catalog.VmSizesByLocation.Values)
        {
            sizes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        await PopulateImagePublishersAsync(subscription, catalog, ct);
        await PopulateImageSkusAsync(subscription, catalog, ct);
        await PopulateAvdSupportAsync(subscription, catalog, ct);

        if (_store is not null)
        {
            // A focused refresh only asked Azure about some regions, so carry forward the image
            // SKUs already on disk for the others. Without this, refreshing from the UI would
            // quietly strip an air-gapped snapshot down to one region.
            if (_imageSkuLocations is not null)
            {
                var previous = await _store.TryLoadAsync(ct);
                if (previous is not null)
                {
                    MergeUnrefreshedImageSkus(catalog, previous, _imageSkuLocations);
                }
            }

            await _store.SaveAsync(catalog, ct);
        }

        return catalog;
    }

    /// <summary>
    /// Carries forward image SKUs the current refresh did not ask Azure about.
    /// </summary>
    /// <remarks>
    /// A refresh focused on one region only knows the truth about that region. Everything it did
    /// not query must be preserved from the snapshot, or a single click in the UI would strip an
    /// air-gapped catalog down to the one region the operator happened to be looking at. Entries
    /// for regions that <em>were</em> refreshed are deliberately dropped, because absence from a
    /// region we just queried is real information: that offer is no longer available there.
    /// </remarks>
    public static void MergeUnrefreshedImageSkus(
        ResourceCatalog current,
        ResourceCatalog previous,
        HashSet<string> refreshedLocations)
    {
        foreach (var (key, skus) in previous.ImageSkus)
        {
            var separator = key.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            var location = key[..separator];
            if (refreshedLocations.Contains(location))
            {
                continue;
            }

            current.ImageSkus.TryAdd(key, skus);
        }

        foreach (var (location, publishers) in previous.ImagePublishersByLocation)
        {
            if (refreshedLocations.Contains(location))
            {
                continue;
            }

            current.ImagePublishersByLocation.TryAdd(location, publishers);
        }
    }

    /// <summary>
    /// Lists every image publisher each region offers - one call per region, unlike the SKU sweep
    /// which is one call per region per publisher/offer pair.
    /// </summary>
    private async Task PopulateImagePublishersAsync(
        SubscriptionResource subscription,
        ResourceCatalog catalog,
        CancellationToken ct)
    {
        var locations = catalog.VmSizesByLocation.Keys
            .Where(l => _imageSkuLocations is null || _imageSkuLocations.Contains(l))
            .ToList();

        using var throttle = new SemaphoreSlim(8);
        var found = new ConcurrentBag<(string Location, List<string> Publishers)>();

        await Task.WhenAll(locations.Select(async location =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var publishers = new List<string>();
                await foreach (var publisher in subscription.GetVirtualMachineImagePublishersAsync(
                    new AzureLocation(location), ct))
                {
                    publishers.Add(publisher.Name);
                }

                if (publishers.Count > 0)
                {
                    publishers.Sort(StringComparer.OrdinalIgnoreCase);
                    found.Add((location, publishers));
                }
            }
            catch (global::Azure.RequestFailedException)
            {
                // Some regions do not serve the marketplace API at all. Not an error: the UI
                // falls back to the built-in list and says so.
            }
            finally
            {
                throttle.Release();
            }
        }));

        foreach (var entry in found)
        {
            catalog.ImagePublishersByLocation[entry.Location] = entry.Publishers;
        }
    }

    private async Task PopulateImageSkusAsync(
        SubscriptionResource subscription,
        ResourceCatalog catalog,
        CancellationToken ct)
    {
        // Only regions that actually have VM sizes are worth querying for images.
        var locations = catalog.VmSizesByLocation.Keys
            .Where(l => _imageSkuLocations is null || _imageSkuLocations.Contains(l))
            .ToList();

        var work = locations
            .SelectMany(location => TrackedImages.Select(image => (location, image.Publisher, image.Offer)))
            .ToList();

        // Bounded concurrency: these are independent read-only calls, and running them one at a
        // time is what made a full refresh take minutes. Kept modest to stay well inside ARM's
        // request throttling.
        using var throttle = new SemaphoreSlim(8);
        var found = new ConcurrentBag<(string Location, string Publisher, string Offer, List<string> Skus)>();

        await Task.WhenAll(work.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var skus = new List<string>();
                await foreach (var sku in subscription.GetVirtualMachineImageSkusAsync(
                    new AzureLocation(item.location), item.Publisher, item.Offer, ct))
                {
                    skus.Add(sku.Name);
                }

                if (skus.Count > 0)
                {
                    found.Add((item.location, item.Publisher, item.Offer, skus));
                }
            }
            catch (global::Azure.RequestFailedException)
            {
                // Publisher/offer simply is not offered in this region. Not an error.
            }
            finally
            {
                throttle.Release();
            }
        }));

        // Applied on one thread: the catalog dictionaries are not thread-safe.
        foreach (var entry in found)
        {
            catalog.SetImageSkus(entry.Location, entry.Publisher, entry.Offer, entry.Skus);
        }
    }

    private static async Task PopulateAvdSupportAsync(
        SubscriptionResource subscription,
        ResourceCatalog catalog,
        CancellationToken ct)
    {
        try
        {
            var provider = await subscription.GetResourceProviderAsync(
                "Microsoft.DesktopVirtualization", cancellationToken: ct);

            var avdRegions = provider.Value.Data.ResourceTypes
                .SelectMany(rt => rt.Locations ?? [])
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < catalog.Locations.Count; i++)
            {
                var location = catalog.Locations[i];
                if (avdRegions.Contains(location.Name) || avdRegions.Contains(Normalize(location.DisplayName)))
                {
                    catalog.Locations[i] = location with { Providers = ["Microsoft.DesktopVirtualization"] };
                }
            }
        }
        catch (global::Azure.RequestFailedException)
        {
            // Provider not registered; leave AVD regions empty rather than failing the refresh.
        }

        static string Normalize(string value) => value.Replace(" ", "").ToLowerInvariant();
    }

    private static List<T> GetOrAdd<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    private static int GetCapabilityInt(ComputeResourceSku sku, string name)
    {
        var capability = sku.Capabilities?.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        return capability is not null && double.TryParse(capability.Value, out var value)
            ? (int)value
            : 0;
    }

    /// <summary>
    /// Reads a True/False capability. Returns <see langword="null"/> when Azure does not report it
    /// at all, which must stay distinct from <see langword="false"/>: "unknown" allows the choice
    /// through, "false" blocks it.
    /// </summary>
    private static bool? GetCapabilityBool(ComputeResourceSku sku, string name)
    {
        var capability = sku.Capabilities?.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        return capability is not null && bool.TryParse(capability.Value, out var value)
            ? value
            : null;
    }
}

/// <summary>
/// Prefers a live refresh, falls back to the on-disk snapshot.
/// </summary>
/// <remarks>
/// This is the behaviour that lets the same build serve a connected workstation and an
/// air-gapped enclave with no configuration difference.
/// </remarks>
public sealed class ResilientCatalogSource : ICatalogSource
{
    private readonly ICatalogSource? _live;
    private readonly CatalogFileStore _store;
    private readonly TimeSpan _maxAge;
    private readonly AzureCloud? _expectedCloud;
    private readonly bool _forceRefresh;

    /// <param name="expectedCloud">
    /// The cloud the caller is signed in to. A snapshot captured against a different cloud is
    /// worse than no snapshot: commercial and US Government share no region names, so serving one
    /// in the other silently offers regions that cannot be deployed to.
    /// </param>
    /// <param name="forceRefresh">
    /// Skips the freshness shortcut. An operator who presses Refresh has asked for a round trip,
    /// and returning a cached snapshot makes the button look broken.
    /// </param>
    public ResilientCatalogSource(
        ICatalogSource? live,
        CatalogFileStore store,
        TimeSpan? maxAge = null,
        AzureCloud? expectedCloud = null,
        bool forceRefresh = false)
    {
        _live = live;
        _store = store;
        _maxAge = maxAge ?? TimeSpan.FromDays(7);
        _expectedCloud = expectedCloud;
        _forceRefresh = forceRefresh;
    }

    /// <summary>Set when the last <see cref="GetAsync"/> served a cached snapshot.</summary>
    public bool ServedFromCache { get; private set; }

    /// <summary>
    /// Set when a snapshot existed but was captured against a different cloud, and was therefore
    /// discarded. The UI uses this to explain an empty region list rather than just showing one.
    /// </summary>
    public bool DiscardedForeignSnapshot { get; private set; }

    public string? LastRefreshError { get; private set; }

    public async Task<ResourceCatalog> GetAsync(CancellationToken ct = default)
    {
        var cached = await _store.TryLoadAsync(ct);

        var usable = cached is not null && MatchesExpectedCloud(cached);
        DiscardedForeignSnapshot = cached is not null && !usable;

        if (usable && !_forceRefresh && !cached!.IsStale(_maxAge))
        {
            ServedFromCache = true;
            return cached;
        }

        if (_live is not null)
        {
            try
            {
                var fresh = await _live.GetAsync(ct);
                ServedFromCache = false;
                LastRefreshError = null;
                DiscardedForeignSnapshot = false;
                return fresh;
            }
            catch (Exception ex)
            {
                LastRefreshError = ex.Message;
            }
        }

        ServedFromCache = true;

        // Deliberately empty rather than the wrong cloud's data. Callers fall back to a small
        // built-in list for the expected cloud, which is narrow but never wrong.
        return usable
            ? cached!
            : new ResourceCatalog { Cloud = (_expectedCloud ?? AzureCloud.Public).ToString() };
    }

    private bool MatchesExpectedCloud(ResourceCatalog catalog)
    {
        if (_expectedCloud is not { } expected)
        {
            return true;
        }

        // An older snapshot written before the cloud was recorded is assumed to be commercial,
        // which is what it would have been.
        var actual = string.IsNullOrWhiteSpace(catalog.Cloud) ? nameof(AzureCloud.Public) : catalog.Cloud;

        return string.Equals(actual, expected.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
