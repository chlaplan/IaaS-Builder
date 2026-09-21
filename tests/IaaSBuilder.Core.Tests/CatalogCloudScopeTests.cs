using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The catalog snapshot is a single file shared by every cloud, and it was being served back
/// without checking which cloud it came from. Signing in to US Government after a commercial
/// session therefore showed commercial regions and never called Azure at all - the snapshot was
/// under the seven day staleness limit, so the refresh short-circuited. The regions looked
/// authoritative and none of them exist in Gov.
/// </summary>
public class CatalogCloudScopeTests
{
    private sealed class StubSource : ICatalogSource
    {
        private readonly ResourceCatalog? _catalog;
        public StubSource(ResourceCatalog? catalog) => _catalog = catalog;

        public int Calls { get; private set; }

        public Task<ResourceCatalog> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            return _catalog is null
                ? throw new InvalidOperationException("live source unavailable")
                : Task.FromResult(_catalog);
        }
    }

    private static ResourceCatalog Snapshot(string cloud, params string[] locations) => new()
    {
        Cloud = cloud,
        CapturedUtc = DateTimeOffset.UtcNow,
        Locations = [.. locations.Select(l => new LocationInfo(l, l, []))]
    };

    private static async Task<CatalogFileStore> StoreWith(ResourceCatalog catalog)
    {
        var dir = Directory.CreateTempSubdirectory("iaasb-catalog").FullName;
        var store = new CatalogFileStore(Path.Combine(dir, "catalog.json"));
        await store.SaveAsync(catalog);
        return store;
    }

    [Fact]
    public async Task A_commercial_snapshot_is_not_served_to_a_government_session()
    {
        var store = await StoreWith(Snapshot("Public", "eastus", "westus2"));

        var source = new ResilientCatalogSource(
            live: null, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        var catalog = await source.GetAsync();

        Assert.True(source.DiscardedForeignSnapshot);
        Assert.DoesNotContain(catalog.Locations, l => l.Name == "eastus");
        Assert.Empty(catalog.Locations);
    }

    /// <summary>
    /// The whole point: a fresh snapshot from the wrong cloud must not stop the live call, which
    /// is what made the region list look permanently stuck on commercial.
    /// </summary>
    [Fact]
    public async Task A_foreign_snapshot_does_not_short_circuit_the_live_refresh()
    {
        var store = await StoreWith(Snapshot("Public", "eastus"));
        var live = new StubSource(Snapshot("UsGovernment", "usgovvirginia"));

        var source = new ResilientCatalogSource(
            live, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        var catalog = await source.GetAsync();

        Assert.Equal(1, live.Calls);
        Assert.Contains(catalog.Locations, l => l.Name == "usgovvirginia");
    }

    [Fact]
    public async Task A_matching_snapshot_is_still_served_without_a_round_trip()
    {
        var store = await StoreWith(Snapshot("Public", "eastus"));
        var live = new StubSource(Snapshot("Public", "westus3"));

        var source = new ResilientCatalogSource(
            live, store, maxAge: null, expectedCloud: AzureCloud.Public);

        var catalog = await source.GetAsync();

        Assert.Equal(0, live.Calls);
        Assert.True(source.ServedFromCache);
        Assert.Contains(catalog.Locations, l => l.Name == "eastus");
    }

    /// <summary>Pressing Refresh is an explicit request for a round trip.</summary>
    [Fact]
    public async Task Force_refresh_bypasses_a_fresh_matching_snapshot()
    {
        var store = await StoreWith(Snapshot("Public", "eastus"));
        var live = new StubSource(Snapshot("Public", "westus3"));

        var source = new ResilientCatalogSource(
            live, store, maxAge: null, expectedCloud: AzureCloud.Public, forceRefresh: true);

        var catalog = await source.GetAsync();

        Assert.Equal(1, live.Calls);
        Assert.Contains(catalog.Locations, l => l.Name == "westus3");
    }

    /// <summary>
    /// If the live call fails we degrade to the snapshot - but only when it is the right cloud.
    /// Falling back to another cloud's regions is worse than falling back to nothing.
    /// Forced so the live source is actually reached; a fresh snapshot would short-circuit first.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_falls_back_to_a_matching_snapshot()
    {
        var store = await StoreWith(Snapshot("Public", "eastus"));
        var source = new ResilientCatalogSource(
            new StubSource(null), store, maxAge: null, expectedCloud: AzureCloud.Public, forceRefresh: true);

        var catalog = await source.GetAsync();

        Assert.True(source.ServedFromCache);
        Assert.NotNull(source.LastRefreshError);
        Assert.Contains(catalog.Locations, l => l.Name == "eastus");
    }

    [Fact]
    public async Task A_failed_refresh_does_not_fall_back_to_a_foreign_snapshot()
    {
        var store = await StoreWith(Snapshot("Public", "eastus"));
        var source = new ResilientCatalogSource(
            new StubSource(null), store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        var catalog = await source.GetAsync();

        Assert.NotNull(source.LastRefreshError);
        Assert.Empty(catalog.Locations);
        Assert.Equal(nameof(AzureCloud.UsGovernment), catalog.Cloud);
    }

    /// <summary>
    /// Offline there is no session and therefore no expected cloud. The snapshot that shipped with
    /// the app is all there is, so it must still be served.
    /// </summary>
    [Fact]
    public async Task With_no_expected_cloud_any_snapshot_is_served()
    {
        var store = await StoreWith(Snapshot("UsGovernment", "usgovvirginia"));
        var source = new ResilientCatalogSource(live: null, store);

        var catalog = await source.GetAsync();

        Assert.False(source.DiscardedForeignSnapshot);
        Assert.Contains(catalog.Locations, l => l.Name == "usgovvirginia");
    }

    /// <summary>A snapshot written before the cloud was recorded can only have been commercial.</summary>
    [Fact]
    public async Task A_snapshot_with_no_recorded_cloud_is_treated_as_commercial()
    {
        var store = await StoreWith(Snapshot("", "eastus"));

        var asPublic = new ResilientCatalogSource(
            live: null, store, maxAge: null, expectedCloud: AzureCloud.Public);
        var asGov = new ResilientCatalogSource(
            live: null, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        await asPublic.GetAsync();
        await asGov.GetAsync();

        Assert.False(asPublic.DiscardedForeignSnapshot);
        Assert.True(asGov.DiscardedForeignSnapshot);
    }
}
