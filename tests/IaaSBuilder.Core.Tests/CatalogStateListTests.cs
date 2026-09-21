using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Image publisher and offer were free text, so the SKU dropdown - keyed on "{region}|{pub}/{offer}"
/// - only ever populated if all three were typed exactly. These cover the derivation of the two
/// new lists from the same keys, so the three dropdowns can never disagree with each other.
///
/// Also covers the region fallback list, which used to mix commercial and US Government regions
/// into one array and offered both regardless of which cloud was signed in to.
/// </summary>
public class CatalogStateListTests
{
    private static CatalogState StateWith(ResourceCatalog catalog, AzureCloud cloud = AzureCloud.Public)
    {
        var dir = Directory.CreateTempSubdirectory("iaasb-state").FullName;
        var state = new CatalogState(
            new CatalogFileStore(Path.Combine(dir, "catalog.json")),
            new AzureSession())
        {
            // The plan's cloud, not the session's. AzureSession.Cloud is only set when a sign-in
            // starts, so while signed out it reads Public whatever the operator picked.
            TargetCloud = cloud
        };

        typeof(CatalogState).GetProperty(nameof(CatalogState.Catalog))!
            .SetValue(state, catalog);

        return state;
    }

    private static ResourceCatalog WithImages(params (string Location, string Publisher, string Offer, string[] Skus)[] entries)
    {
        var catalog = new ResourceCatalog();
        foreach (var (location, publisher, offer, skus) in entries)
        {
            catalog.SetImageSkus(location, publisher, offer, skus);
        }

        return catalog;
    }

    [Fact]
    public void Publishers_come_from_the_catalog_for_the_selected_region()
    {
        var state = StateWith(WithImages(
            ("eastus", "MicrosoftWindowsServer", "WindowsServer", ["2022-datacenter"]),
            ("eastus", "MicrosoftWindowsDesktop", "Windows-11", ["win11-24h2-ent"])));

        var publishers = state.ImagePublishers("eastus");

        Assert.Contains("MicrosoftWindowsServer", publishers);
        Assert.Contains("MicrosoftWindowsDesktop", publishers);
    }

    /// <summary>Image availability is per region, so another region's publishers must not leak in.</summary>
    [Fact]
    public void Publishers_from_another_region_are_not_offered()
    {
        var state = StateWith(WithImages(
            ("eastus", "MicrosoftWindowsServer", "WindowsServer", ["2022-datacenter"]),
            ("westus2", "MicrosoftSQLServer", "sql2022-ws2022", ["enterprise"])));

        Assert.DoesNotContain("MicrosoftSQLServer", state.ImagePublishers("eastus"));
    }

    [Fact]
    public void Offers_are_filtered_to_the_chosen_publisher()
    {
        var state = StateWith(WithImages(
            ("eastus", "MicrosoftWindowsDesktop", "Windows-11", ["win11-24h2-ent"]),
            ("eastus", "MicrosoftWindowsDesktop", "Windows-10", ["win10-22h2-ent"]),
            ("eastus", "MicrosoftWindowsServer", "WindowsServer", ["2022-datacenter"])));

        var offers = state.ImageOffers("eastus", "MicrosoftWindowsDesktop");

        Assert.Equal(["Windows-10", "Windows-11"], offers);
    }

    /// <summary>
    /// The three dropdowns are keyed off one another, so a publisher/offer pair the user can pick
    /// must always yield the SKU list it came from.
    /// </summary>
    [Fact]
    public void Every_offered_publisher_and_offer_resolves_to_skus()
    {
        var state = StateWith(WithImages(
            ("eastus", "MicrosoftWindowsServer", "WindowsServer", ["2019-datacenter", "2022-datacenter"]),
            ("eastus", "MicrosoftSharePoint", "MicrosoftSharePointServer", ["sp2019"])));

        foreach (var publisher in state.ImagePublishers("eastus"))
        {
            foreach (var offer in state.ImageOffers("eastus", publisher))
            {
                Assert.NotEmpty(state.ImageSkus("eastus", publisher, offer));
            }
        }
    }

    [Fact]
    public void No_publisher_selected_yields_no_offers()
    {
        var state = StateWith(WithImages(
            ("eastus", "MicrosoftWindowsServer", "WindowsServer", ["2022-datacenter"])));

        Assert.Empty(state.ImageOffers("eastus", ""));
    }

    [Fact]
    public void Government_sessions_are_not_offered_commercial_regions()
    {
        var state = StateWith(new ResourceCatalog(), AzureCloud.UsGovernment);

        var regions = state.LocationNames;

        Assert.All(regions, r => Assert.StartsWith("usgov", r, StringComparison.Ordinal));
        Assert.DoesNotContain("eastus", regions);
    }

    [Fact]
    public void Commercial_sessions_are_not_offered_government_regions()
    {
        var state = StateWith(new ResourceCatalog());

        Assert.DoesNotContain(state.LocationNames, r => r.StartsWith("usgov", StringComparison.Ordinal));
    }

    /// <summary>
    /// An enclave's region names are unknowable from here, and guessing commercial ones would be
    /// worse than an empty list the operator has to type into.
    /// </summary>
    [Fact]
    public void A_custom_cloud_gets_no_guessed_regions()
    {
        var state = StateWith(new ResourceCatalog(), AzureCloud.Custom);

        Assert.Empty(state.LocationNames);
    }

    /// <summary>Real catalog data always wins over the built-in list.</summary>
    [Fact]
    public void Catalog_regions_override_the_fallback()
    {
        var catalog = new ResourceCatalog
        {
            Locations = [new LocationInfo("usgovtexas", "USGov Texas", [])]
        };

        var state = StateWith(catalog, AzureCloud.UsGovernment);

        Assert.Equal(["usgovtexas"], state.LocationNames);
    }
}
