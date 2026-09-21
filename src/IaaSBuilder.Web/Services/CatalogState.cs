using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// Supplies the dropdown data (regions, VM sizes, image SKUs, dedicated host SKUs).
/// </summary>
/// <remarks>
/// The legacy form called <c>Get-AzVMSize</c> and <c>Get-AzVMImageSku</c> from a
/// SelectionChanged handler every time a region changed, so the UI froze on every change and
/// was completely unusable with no connection. Here the catalog is a snapshot on disk that is
/// refreshed opportunistically, so the same build works connected and disconnected.
/// </remarks>
public sealed class CatalogState
{
    private readonly CatalogFileStore _store;
    private readonly AzureSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CatalogState(CatalogFileStore store, AzureSession session)
    {
        _store = store;
        _session = session;
    }

    public ResourceCatalog Catalog { get; private set; } = new();
    public bool ServedFromCache { get; private set; } = true;
    public bool HasSnapshot => _store.Exists;

    /// <summary>
    /// The cloud the plan is targeting, which is not the same thing as the cloud of the current
    /// sign-in. <see cref="AzureSession.Cloud"/> is only set when a sign-in *starts*, so while
    /// signed out it reads Public no matter what the operator picked - which made every fallback
    /// region list commercial. Pushed in by <see cref="PlanState"/> whenever the plan changes;
    /// CatalogState cannot depend on PlanState directly because PlanState already depends on it.
    /// </summary>
    public AzureCloud TargetCloud
    {
        get => _targetCloud;
        set
        {
            if (_targetCloud == value)
            {
                return;
            }

            _targetCloud = value;
            Changed?.Invoke();
        }
    }

    private AzureCloud _targetCloud = AzureCloud.Public;
    public string SnapshotPath => _store.Path;
    public string? LastError { get; private set; }
    public bool IsRefreshing { get; private set; }

    public event Action? Changed;

    private bool _loadedOffline;

    /// <summary>
    /// Loads the snapshot the first time a circuit asks for it. Replaces the old startup warm-up,
    /// which resolved this service from the root provider and would now throw because it is scoped.
    /// Reading a local JSON file once per circuit is cheap.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loadedOffline)
        {
            return;
        }

        _loadedOffline = true;
        await LoadOfflineAsync(ct);
    }

    /// <summary>Loads the on-disk snapshot without touching the network.</summary>
    public async Task LoadOfflineAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await new OfflineCatalogSource(_store).GetAsync(ct);

            // The snapshot is a single shared catalog.json - deliberately, so one file serves an
            // air-gapped copy of the app. That means it can easily hold another cloud's data from
            // a previous session. Serving it here would show US Government regions while the
            // operator has Azure commercial selected, and look authoritative doing it.
            var foreign = !MatchesTargetCloud(snapshot);

            Catalog = foreign ? new ResourceCatalog() : snapshot;
            DiscardedForeignSnapshot = foreign;
            ServedFromCache = true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Refreshes from Azure when signed in, falling back to the snapshot on any failure so a
    /// dropped connection degrades to stale data rather than an empty form.
    /// </summary>
    /// <param name="focusLocation">
    /// The region to fetch image SKUs for. The UI passes the region being deployed to, which turns
    /// a multi-minute sweep of every region into a handful of calls. Pass null to refresh them all,
    /// which is what building an offline snapshot wants.
    /// </param>
    public async Task RefreshAsync(
        string subscriptionId,
        string? focusLocation = null,
        bool force = false,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        IsRefreshing = true;
        LastError = null;
        Changed?.Invoke();

        try
        {
            var signedIn = _session.IsSignedIn && !string.IsNullOrWhiteSpace(subscriptionId);

            ICatalogSource? live = signedIn
                ? new AzureCatalogSource(
                    _session.Credential,
                    _session.Cloud,
                    subscriptionId,
                    _store,
                    string.IsNullOrWhiteSpace(focusLocation) ? null : [focusLocation])
                : null;

            // Only constrain the snapshot to a cloud once we actually know which one we are on.
            // Offline, the snapshot that shipped with the app is all there is.
            var resilient = new ResilientCatalogSource(
                live,
                _store,
                maxAge: null,
                expectedCloud: signedIn ? _session.Cloud : null,
                forceRefresh: force);

            Catalog = await resilient.GetAsync(ct);
            ServedFromCache = resilient.ServedFromCache;
            DiscardedForeignSnapshot = resilient.DiscardedForeignSnapshot;
            LastError = resilient.LastRefreshError;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Clears catalog data that belongs to a different cloud. Called when the cloud target
    /// changes, so region and size lists never carry over - commercial and US Government share no
    /// region names, and an inherited list looks authoritative while being entirely wrong.
    /// </summary>
    public async Task ClearForCloudChangeAsync(CancellationToken ct = default)
    {
        Catalog = new ResourceCatalog();
        ServedFromCache = true;
        DiscardedForeignSnapshot = false;
        LastError = null;
        _loadedOffline = false;
        Changed?.Invoke();

        // Re-read the snapshot against the new cloud. If it belongs to this cloud the real region
        // list comes back; if not it is discarded and the built-in fallback list is used.
        await EnsureLoadedAsync(ct);
    }

    /// <summary>
    /// True when a snapshot existed but belonged to another cloud and was discarded. Regions then
    /// come from the small built-in list until a refresh succeeds.
    /// </summary>
    public bool DiscardedForeignSnapshot { get; private set; }

    /// <summary>The cloud whose data is currently loaded, for display.</summary>
    public string CatalogCloud => Catalog.Cloud;

    /// <summary>
    /// True when the catalog holds data for <see cref="TargetCloud"/>. A blank cloud means a
    /// snapshot written before catalogs recorded one, which is treated as commercial.
    /// </summary>
    private bool MatchesTargetCloud(ResourceCatalog catalog)
    {
        if (catalog.Locations.Count == 0)
        {
            return true;
        }

        var actual = string.IsNullOrWhiteSpace(catalog.Cloud)
            ? nameof(AzureCloud.Public)
            : catalog.Cloud;

        return string.Equals(actual, TargetCloud.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> LocationNames =>
        Catalog.Locations.Count > 0
            ? Catalog.Locations.Select(l => l.Name).ToList()
            : FallbackLocationsFor(TargetCloud);

    public IReadOnlyList<string> VmSizeNames(string location)
    {
        var sizes = Deployable(Catalog.GetVmSizes(location));
        return sizes.Count > 0 ? sizes.Select(s => s.Name).ToList() : FallbackVmSizes;
    }

    /// <summary>
    /// Full details for every size the region offers, so the dropdown can show cores, memory and
    /// price rather than a bare SKU name, and so the disk dropdown can tell which sizes support
    /// Premium SSD.
    /// </summary>
    /// <remarks>
    /// The fallback list carries no capabilities at all - <see cref="VmSizeInfo.PremiumIo"/> stays
    /// null - which is correct: with no catalog the tool genuinely does not know, and unknown must
    /// not be treated as "cannot".
    /// </remarks>
    public IReadOnlyList<VmSizeInfo> VmSizes(string location)
    {
        var sizes = Deployable(Catalog.GetVmSizes(location));

        return sizes.Count > 0
            ? sizes
            : FallbackVmSizes.Select(name => new VmSizeInfo(name, 0, 0, 0)).ToList();
    }

    /// <summary>
    /// True when there are sizes to show but none of them carry a definite answer about whether
    /// this subscription may deploy them, so the size list is necessarily unfiltered.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than hiding. Signed in, a refresh fixes it and the operator should
    /// be told to do that; air-gapped, no refresh is possible and the operator needs to know the
    /// dropdown is listing everything the region has rather than everything they can have.
    /// </remarks>
    public bool AvailabilityDataMissing =>
        Catalog.VmSizesByLocation.Count > 0 && !Catalog.HasAvailabilityData;

    /// <summary>
    /// Every size the region lists, including ones this subscription may not deploy.
    /// </summary>
    /// <remarks>
    /// Needed because a plan file can already name a restricted size, and the page still has to be
    /// able to look it up to explain itself. Never use this to populate a dropdown.
    /// </remarks>
    public IReadOnlyList<VmSizeInfo> AllVmSizes(string location) =>
        Catalog.GetVmSizes(location);

    /// <summary>
    /// Drops sizes Azure has positively said this subscription cannot deploy here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offering them was worse than useless: Azure refuses the virtual machine with
    /// <c>SkuNotAvailable</c>, and for one real subscription in usgovvirginia that is 73 of the 952
    /// sizes the region reports. "Available in this region" and "available to you in this region"
    /// are different questions and the dropdown should only ever answer the second.
    /// </para>
    /// <para>
    /// Only drops a positive statement of restriction. A snapshot captured before this field
    /// existed leaves it null everywhere, and unknown has to mean "offer it" - the alternative is
    /// an air-gapped enclave with an older catalog showing an empty size list.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<VmSizeInfo> Deployable(IReadOnlyList<VmSizeInfo> sizes)
    {
        if (!sizes.Any(s => s.KnownUnavailable))
        {
            return sizes;
        }

        return sizes.Where(s => !s.KnownUnavailable).ToList();
    }

    /// <summary>
    /// The catalog entry for one size, or <see langword="null"/> when the region has no catalog
    /// data or the size is not in it.
    /// </summary>
    public VmSizeInfo? VmSize(string location, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Catalog.GetVmSizes(location)
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// SKUs for a publisher/offer pair. Falls back to the built-in list when the catalog has
    /// nothing, so the dropdown is never empty for an image this tool ships a role for.
    /// </summary>
    public IReadOnlyList<string> ImageSkus(string location, string publisher, string offer)
    {
        var skus = Catalog.GetImageSkus(location, publisher, offer);
        return skus.Count > 0 ? skus : KnownImages.SkusFor(publisher, offer);
    }

    /// <summary>
    /// Image publishers this region offers. Prefers the full list Azure reported - several hundred
    /// in a commercial region - and only falls back to deriving one from the SKU keys, which holds
    /// just the handful of pairs this tool ships roles for.
    /// </summary>
    public IReadOnlyList<string> ImagePublishers(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return KnownImages.Publishers;
        }

        var all = Catalog.GetImagePublishers(location);
        if (all.Count > 0)
        {
            return all;
        }

        var found = ImageKeysFor(location)
            .Select(k => k.Publisher)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return found.Count > 0 ? found : KnownImages.Publishers;
    }

    /// <summary>
    /// True when the image lists for this region are the built-in ones rather than anything the
    /// catalog actually observed, so the UI can say so instead of presenting guesses as fact.
    /// </summary>
    public bool ImagePublishersAreBuiltIn(string location) =>
        Catalog.GetImagePublishers(location).Count == 0 && !ImageKeysFor(location).Any();

    /// <summary>Offers for a publisher in this region, from the same keys as the SKU list.</summary>
    public IReadOnlyList<string> ImageOffers(string location, string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return [];
        }

        var found = ImageKeysFor(location)
            .Where(k => string.Equals(k.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Offer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return found.Count > 0 ? found : KnownImages.OffersFor(publisher);
    }

    /// <summary>
    /// Splits the "{location}|{publisher}/{offer}" keys the catalog stores. The offer may itself
    /// contain a '/', so only the first separator is significant.
    /// </summary>
    private IEnumerable<(string Publisher, string Offer)> ImageKeysFor(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            yield break;
        }

        var prefix = location + "|";

        foreach (var key in Catalog.ImageSkus.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash > 0 && slash < rest.Length - 1)
            {
                yield return (rest[..slash], rest[(slash + 1)..]);
            }
        }
    }

    public IReadOnlyList<string> AvdLocations
    {
        get
        {
            var avd = Catalog.GetAvdLocations().Select(l => l.Name).ToList();
            return avd.Count > 0 ? avd : FallbackAvdLocationsFor(TargetCloud);
        }
    }

    /// <summary>
    /// Used when there is no usable snapshot, so a first run - or a first run against a cloud the
    /// snapshot was not captured in - still presents a narrow but correct set of choices rather
    /// than either an empty list or another cloud's regions.
    /// </summary>
    private static IReadOnlyList<string> FallbackLocationsFor(AzureCloud cloud) => cloud switch
    {
        AzureCloud.UsGovernment => ["usgovvirginia", "usgovarizona", "usgovtexas", "usgoviowa"],
        AzureCloud.China => ["chinanorth3", "chinaeast3", "chinanorth2", "chinaeast2"],
        // A custom/air-gapped cloud has region names we cannot know; guessing commercial ones
        // would be actively misleading, so offer nothing and let the operator type.
        AzureCloud.Custom => [],
        _ => ["eastus", "eastus2", "centralus", "westus2", "westus3"]
    };

    private static IReadOnlyList<string> FallbackAvdLocationsFor(AzureCloud cloud) => cloud switch
    {
        AzureCloud.UsGovernment => ["usgovvirginia", "usgovarizona"],
        AzureCloud.China => [],
        AzureCloud.Custom => [],
        _ => ["eastus", "eastus2", "centralus", "westus2"]
    };

    private static readonly string[] FallbackVmSizes =
    [
        "Standard_B2ms", "Standard_D2s_v5", "Standard_D4s_v5", "Standard_D8s_v5",
        "Standard_E4s_v5", "Standard_E8s_v5", "Standard_F2s_v2", "Standard_F4s_v2"
    ];
}
