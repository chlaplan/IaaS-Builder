using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The size dropdown used to offer every size the region reported, including the ones Azure
/// positively refuses this subscription. The operator duly picked one, and the deployment was
/// blocked - correctly, but only after they had made a choice the tool should never have offered.
/// "Available in this region" and "available to you in this region" are different questions.
/// </summary>
public class RestrictedSizeDropdownTests
{
    private const string Region = "usgovvirginia";

    private static CatalogState StateWith(params VmSizeInfo[] sizes)
    {
        var catalog = new ResourceCatalog();
        catalog.VmSizesByLocation[Region] = [.. sizes];

        var dir = Directory.CreateTempSubdirectory("iaasb-restricted").FullName;
        var state = new CatalogState(
            new CatalogFileStore(Path.Combine(dir, "catalog.json")),
            new AzureSession());

        typeof(CatalogState).GetProperty(nameof(CatalogState.Catalog))!
            .SetValue(state, catalog);

        return state;
    }

    private static VmSizeInfo Size(string name, bool? restricted = null) =>
        new(name, 2, 8192, 4, PremiumIo: true, Restricted: restricted);

    [Fact]
    public void A_restricted_size_is_not_offered()
    {
        var state = StateWith(Size("Standard_D2s_v5"), Size("Standard_D1", restricted: true));

        Assert.DoesNotContain("Standard_D1", state.VmSizeNames(Region));
        Assert.DoesNotContain(state.VmSizes(Region), s => s.Name == "Standard_D1");
    }

    [Fact]
    public void A_deployable_size_is_still_offered()
    {
        var state = StateWith(Size("Standard_D2s_v5"), Size("Standard_D1", restricted: true));

        Assert.Contains("Standard_D2s_v5", state.VmSizeNames(Region));
    }

    [Fact]
    public void An_explicitly_unrestricted_size_is_offered()
    {
        var state = StateWith(Size("Standard_D2s_v5", restricted: false));

        Assert.Contains("Standard_D2s_v5", state.VmSizeNames(Region));
    }

    /// <summary>
    /// A snapshot captured before the field existed leaves Restricted null on every size. Treating
    /// unknown as restricted would empty the dropdown in an air-gapped enclave, which is a worse
    /// failure than the one being fixed - there is no way to refresh in there.
    /// </summary>
    [Fact]
    public void Unknown_is_offered_because_an_old_snapshot_knows_nothing_about_restrictions()
    {
        var state = StateWith(Size("Standard_D2s_v5"), Size("Standard_D4s_v5"));

        Assert.Equal(2, state.VmSizes(Region).Count);
    }

    /// <summary>
    /// The page still has to be able to resolve a restricted size, or a plan file naming one shows
    /// a blank field instead of an explanation.
    /// </summary>
    [Fact]
    public void The_unfiltered_list_still_contains_restricted_sizes()
    {
        var state = StateWith(Size("Standard_D2s_v5"), Size("Standard_D1", restricted: true));

        Assert.Contains(state.AllVmSizes(Region), s => s.Name == "Standard_D1");
        Assert.NotNull(state.VmSize(Region, "Standard_D1"));
    }
}
