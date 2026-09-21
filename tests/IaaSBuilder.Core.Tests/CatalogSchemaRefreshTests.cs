using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The restricted-size filter shipped and did nothing, because the data it needs was never
/// fetched. The operator's snapshot was two days old - comfortably inside the seven day staleness
/// limit - so the refresh short-circuited and returned a snapshot written by a build that had
/// never heard of <see cref="VmSizeInfo.Restricted"/>. Every size in the region was therefore
/// reported as unrestricted, the dropdown offered sizes Azure refuses, and nothing on screen
/// suggested refreshing would help: by every visible measure the catalog was fresh.
///
/// Age is the wrong question on its own. A field that is absent is not a field that says "no".
/// </summary>
public class CatalogSchemaRefreshTests
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

    private static async Task<CatalogFileStore> StoreWith(ResourceCatalog catalog)
    {
        var dir = Directory.CreateTempSubdirectory("iaasb-schema").FullName;
        var store = new CatalogFileStore(Path.Combine(dir, "catalog.json"));
        await store.SaveAsync(catalog);
        return store;
    }

    /// <summary>A snapshot exactly as an older build would have written it: recent, no Restricted.</summary>
    private static ResourceCatalog OldSchemaSnapshot() => new()
    {
        SchemaVersion = "1.0",
        Cloud = nameof(AzureCloud.UsGovernment),
        CapturedUtc = DateTimeOffset.UtcNow.AddDays(-2),
        Locations = [new LocationInfo("usgovvirginia", "USGov Virginia", [])],
        VmSizesByLocation =
        {
            ["usgovvirginia"] = [new VmSizeInfo("Standard_D1", 1, 3584, 4)]
        }
    };

    private static ResourceCatalog CurrentSchemaSnapshot() => new()
    {
        Cloud = nameof(AzureCloud.UsGovernment),
        CapturedUtc = DateTimeOffset.UtcNow.AddDays(-2),
        Locations = [new LocationInfo("usgovvirginia", "USGov Virginia", [])],
        VmSizesByLocation =
        {
            ["usgovvirginia"] =
            [
                new VmSizeInfo("Standard_D1", 1, 3584, 4, PremiumIo: false, Restricted: true),
                new VmSizeInfo("Standard_D2s_v5", 2, 8192, 4, PremiumIo: true, Restricted: false)
            ]
        }
    };

    [Fact]
    public void A_snapshot_from_an_older_build_is_recognised_as_out_of_date()
    {
        Assert.True(OldSchemaSnapshot().PredatesCurrentSchema);
        Assert.False(CurrentSchemaSnapshot().PredatesCurrentSchema);
    }

    /// <summary>
    /// The exact condition that hid the bug: recent enough to pass every age check there is.
    /// </summary>
    [Fact]
    public void The_stale_old_schema_snapshot_is_not_stale_by_age()
    {
        Assert.False(OldSchemaSnapshot().IsStale(TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task A_recent_snapshot_from_an_older_build_is_refreshed_anyway()
    {
        var store = await StoreWith(OldSchemaSnapshot());
        var live = new StubSource(CurrentSchemaSnapshot());

        var source = new ResilientCatalogSource(
            live, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        var catalog = await source.GetAsync();

        Assert.Equal(1, live.Calls);
        Assert.False(source.ServedFromCache);
        Assert.True(catalog.HasAvailabilityData);
    }

    [Fact]
    public async Task A_recent_snapshot_from_this_build_is_still_served_without_a_round_trip()
    {
        var store = await StoreWith(CurrentSchemaSnapshot());
        var live = new StubSource(CurrentSchemaSnapshot());

        var source = new ResilientCatalogSource(
            live, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        await source.GetAsync();

        Assert.Equal(0, live.Calls);
        Assert.True(source.ServedFromCache);
    }

    /// <summary>
    /// The air-gapped case, and the reason an old-schema snapshot is never discarded. There is no
    /// refresh available in an enclave, so an unfiltered size list is strictly better than none.
    /// </summary>
    [Fact]
    public async Task An_old_schema_snapshot_is_still_served_when_there_is_no_way_to_refresh()
    {
        var store = await StoreWith(OldSchemaSnapshot());

        var source = new ResilientCatalogSource(
            live: null, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        var catalog = await source.GetAsync();

        Assert.True(source.ServedFromCache);
        Assert.Contains(catalog.GetVmSizes("usgovvirginia"), s => s.Name == "Standard_D1");
    }

    /// <summary>
    /// A failed refresh must not leave the operator worse off than not trying.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_falls_back_to_the_old_schema_snapshot()
    {
        var store = await StoreWith(OldSchemaSnapshot());
        var live = new StubSource(null);

        var source = new ResilientCatalogSource(
            live, store, maxAge: null, expectedCloud: AzureCloud.UsGovernment);

        var catalog = await source.GetAsync();

        Assert.Equal(1, live.Calls);
        Assert.True(source.ServedFromCache);
        Assert.NotNull(source.LastRefreshError);
        Assert.Contains(catalog.GetVmSizes("usgovvirginia"), s => s.Name == "Standard_D1");
    }

    [Fact]
    public void Availability_data_is_absent_from_an_old_snapshot_and_present_in_a_new_one()
    {
        Assert.False(OldSchemaSnapshot().HasAvailabilityData);
        Assert.True(CurrentSchemaSnapshot().HasAvailabilityData);
    }

    /// <summary>
    /// "Nothing is restricted here" is a real answer and must not be mistaken for "no data".
    /// Otherwise a region that genuinely restricts nothing would permanently claim its size list
    /// is unfiltered.
    /// </summary>
    [Fact]
    public void A_region_that_restricts_nothing_still_counts_as_having_data()
    {
        var catalog = new ResourceCatalog
        {
            VmSizesByLocation =
            {
                ["usgovvirginia"] = [new VmSizeInfo("Standard_D2s_v5", 2, 8192, 4, true, Restricted: false)]
            }
        };

        Assert.True(catalog.HasAvailabilityData);
    }
}
