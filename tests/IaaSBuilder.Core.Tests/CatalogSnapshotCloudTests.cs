using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Two bugs found by driving the real UI, both of which made the region list show the wrong
/// cloud's regions while looking completely normal:
///
/// 1. <c>LoadOfflineAsync</c> read catalog.json with no cloud check at all. The snapshot is a
///    single shared file by design (one file has to serve an air-gapped copy of the app), so
///    after one US Government session every fresh circuit showed usgov regions - even with
///    Azure commercial selected, and even though the cloud-scoping fix was already in
///    <c>ResilientCatalogSource</c>. Only the live-refresh path was covered.
///
/// 2. The fallback region lists were keyed on <c>AzureSession.Cloud</c>, which is only assigned
///    when a sign-in *begins*. Choosing a cloud and not signing in left it at Public, so picking
///    US Government offered eastus/westus2 and picking Custom - which must offer nothing, because
///    guessing region names for an enclave is worse than offering none - offered commercial ones.
/// </summary>
public class CatalogSnapshotCloudTests
{
    private static (CatalogState State, CatalogFileStore Store) NewState(AzureCloud target)
    {
        var dir = Directory.CreateTempSubdirectory("iaasb-snap").FullName;
        var store = new CatalogFileStore(Path.Combine(dir, "catalog.json"));

        return (new CatalogState(store, new AzureSession()) { TargetCloud = target }, store);
    }

    private static async Task WriteSnapshotAsync(CatalogFileStore store, string cloud, params string[] locations)
    {
        var catalog = new ResourceCatalog { Cloud = cloud };
        foreach (var location in locations)
        {
            catalog.Locations.Add(new LocationInfo(location, location, ["Microsoft.Compute"]));
        }

        await store.SaveAsync(catalog);
    }

    [Fact]
    public async Task A_snapshot_from_another_cloud_is_not_served_offline()
    {
        var (state, store) = NewState(AzureCloud.Public);
        await WriteSnapshotAsync(store, nameof(AzureCloud.UsGovernment), "usgovvirginia", "usgovarizona");

        await state.EnsureLoadedAsync();

        Assert.True(state.DiscardedForeignSnapshot);
        Assert.Empty(state.Catalog.Locations);
        Assert.DoesNotContain("usgovvirginia", state.LocationNames);
        Assert.Contains("eastus", state.LocationNames);
    }

    [Fact]
    public async Task A_snapshot_from_the_targeted_cloud_is_served_offline()
    {
        var (state, store) = NewState(AzureCloud.UsGovernment);
        await WriteSnapshotAsync(store, nameof(AzureCloud.UsGovernment), "usgovvirginia", "usgovarizona");

        await state.EnsureLoadedAsync();

        Assert.False(state.DiscardedForeignSnapshot);
        Assert.Contains("usgovvirginia", state.LocationNames);
        Assert.DoesNotContain("eastus", state.LocationNames);
    }

    [Fact]
    public async Task A_snapshot_with_no_recorded_cloud_is_treated_as_commercial()
    {
        var (state, store) = NewState(AzureCloud.Public);
        await WriteSnapshotAsync(store, "", "eastus");

        await state.EnsureLoadedAsync();

        Assert.False(state.DiscardedForeignSnapshot);
        Assert.Contains("eastus", state.LocationNames);
    }

    [Fact]
    public async Task Changing_cloud_re_reads_the_snapshot_for_the_new_cloud()
    {
        var (state, store) = NewState(AzureCloud.Public);
        await WriteSnapshotAsync(store, nameof(AzureCloud.UsGovernment), "usgovvirginia");

        await state.EnsureLoadedAsync();
        Assert.DoesNotContain("usgovvirginia", state.LocationNames);

        state.TargetCloud = AzureCloud.UsGovernment;
        await state.ClearForCloudChangeAsync();

        Assert.Contains("usgovvirginia", state.LocationNames);
        Assert.False(state.DiscardedForeignSnapshot);
    }

    [Theory]
    [InlineData(AzureCloud.Public, "eastus")]
    [InlineData(AzureCloud.UsGovernment, "usgovvirginia")]
    public void The_fallback_region_list_follows_the_target_cloud_with_no_sign_in(
        AzureCloud cloud,
        string expected)
    {
        var (state, _) = NewState(cloud);

        Assert.Contains(expected, state.LocationNames);
    }

    [Fact]
    public void A_custom_cloud_offers_no_guessed_regions()
    {
        var (state, _) = NewState(AzureCloud.Custom);

        Assert.Empty(state.LocationNames);
        Assert.Empty(state.AvdLocations);
    }

    [Fact]
    public void Changing_the_target_cloud_notifies_subscribers()
    {
        var (state, _) = NewState(AzureCloud.Public);
        var raised = 0;
        state.Changed += () => raised++;

        state.TargetCloud = AzureCloud.UsGovernment;
        state.TargetCloud = AzureCloud.UsGovernment;

        Assert.Equal(1, raised);
    }
}
