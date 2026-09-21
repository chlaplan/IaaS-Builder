using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The image dropdowns dead-ended. Publishers had a hard-coded fallback for when the catalog
/// held nothing for a region, but offers and SKUs had none - so selecting a publisher from that
/// fallback produced two empty dropdowns and the operator was back to typing exact marketplace
/// strings from memory, which is what the dropdowns existed to prevent.
///
/// These cover the built-in list itself, the chain never dead-ending, and the invariant that the
/// images this tool ships as defaults actually appear in their own dropdowns.
/// </summary>
public class KnownImageTests
{
    private static CatalogState StateWith(ResourceCatalog catalog)
    {
        var dir = Directory.CreateTempSubdirectory("iaasb-known").FullName;
        var state = new CatalogState(
            new CatalogFileStore(Path.Combine(dir, "catalog.json")),
            new AzureSession());

        typeof(CatalogState).GetProperty(nameof(CatalogState.Catalog))!
            .SetValue(state, catalog);

        return state;
    }

    [Fact]
    public void Every_known_publisher_has_at_least_one_offer_with_skus()
    {
        Assert.NotEmpty(KnownImages.Publishers);

        foreach (var publisher in KnownImages.Publishers)
        {
            var offers = KnownImages.OffersFor(publisher);
            Assert.NotEmpty(offers);

            foreach (var offer in offers)
            {
                Assert.NotEmpty(KnownImages.SkusFor(publisher, offer));
            }
        }
    }

    [Fact]
    public void Unknown_publisher_and_offer_yield_empty_lists()
    {
        Assert.Empty(KnownImages.OffersFor("SomeThirdParty"));
        Assert.Empty(KnownImages.SkusFor("MicrosoftWindowsServer", "NotAnOffer"));
        Assert.Empty(KnownImages.OffersFor(null));
        Assert.Empty(KnownImages.OffersFor("  "));
        Assert.Empty(KnownImages.SkusFor("MicrosoftWindowsServer", null));
    }

    [Fact]
    public void Lookups_ignore_case_because_the_operator_can_type_the_publisher()
    {
        Assert.NotEmpty(KnownImages.OffersFor("microsoftwindowsserver"));
        Assert.NotEmpty(KnownImages.SkusFor("MICROSOFTWINDOWSSERVER", "windowsserver"));
    }

    [Fact]
    public void Pairs_covers_every_publisher_and_offer()
    {
        var expected = KnownImages.Publishers
            .SelectMany(p => KnownImages.OffersFor(p).Select(o => (p, o)))
            .ToList();

        Assert.Equal(expected.Count, KnownImages.Pairs.Count);
        foreach (var pair in expected)
        {
            Assert.Contains(pair, KnownImages.Pairs);
        }
    }

    /// <summary>
    /// A default SKU missing from its own dropdown would mean the field opens showing a value the
    /// list does not contain, which reads as "this is wrong" for the one value known to be right.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefaultImages))]
    public void Default_images_appear_in_their_own_dropdowns(string publisher, string offer, string sku)
    {
        Assert.Contains(publisher, KnownImages.Publishers);
        Assert.Contains(offer, KnownImages.OffersFor(publisher));
        Assert.Contains(sku, KnownImages.SkusFor(publisher, offer));
    }

    public static TheoryData<string, string, string> DefaultImages()
    {
        var data = new TheoryData<string, string, string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(ImageReferenceSpec image)
        {
            if (seen.Add($"{image.Publisher}/{image.Offer}/{image.Sku}"))
            {
                data.Add(image.Publisher, image.Offer, image.Sku);
            }
        }

        Add(ImageDefaults.WindowsServer);
        Add(ImageDefaults.WindowsClient);
        Add(ImageDefaults.AvdSessionHost);

        foreach (var role in RoleCatalog.All)
        {
            Add(role.DefaultImage);
        }

        return data;
    }

    [Fact]
    public void Publisher_offer_and_sku_all_fall_back_when_the_catalog_is_empty()
    {
        var state = StateWith(new ResourceCatalog());

        var publishers = state.ImagePublishers("eastus");
        Assert.NotEmpty(publishers);

        // The chain has to hold all the way down: a publisher the UI offers must lead to offers,
        // and each of those to SKUs. Only the first hop used to work.
        foreach (var publisher in publishers)
        {
            var offers = state.ImageOffers("eastus", publisher);
            Assert.NotEmpty(offers);

            foreach (var offer in offers)
            {
                Assert.NotEmpty(state.ImageSkus("eastus", publisher, offer));
            }
        }
    }

    [Fact]
    public void Observed_catalog_data_wins_over_the_built_in_list()
    {
        var catalog = new ResourceCatalog();
        catalog.SetImageSkus("eastus", "MicrosoftWindowsServer", "WindowsServer", ["only-this-one"]);

        var state = StateWith(catalog);

        Assert.Equal(["only-this-one"], state.ImageSkus("eastus", "MicrosoftWindowsServer", "WindowsServer"));
        Assert.Equal(["WindowsServer"], state.ImageOffers("eastus", "MicrosoftWindowsServer"));
        Assert.False(state.ImagePublishersAreBuiltIn("eastus"));
    }

    /// <summary>
    /// The catalog holding data for one region says nothing about another, so the region the
    /// operator is actually deploying to must still get the built-in list.
    /// </summary>
    [Fact]
    public void A_region_with_no_catalog_data_still_falls_back()
    {
        var catalog = new ResourceCatalog();
        catalog.SetImageSkus("eastus", "MicrosoftWindowsServer", "WindowsServer", ["only-this-one"]);

        var state = StateWith(catalog);

        Assert.True(state.ImagePublishersAreBuiltIn("usgovvirginia"));
        Assert.Contains("MicrosoftSQLServer", state.ImagePublishers("usgovvirginia"));
        Assert.NotEmpty(state.ImageSkus("usgovvirginia", "MicrosoftWindowsServer", "WindowsServer"));
    }

    [Fact]
    public void Offers_stay_empty_until_a_publisher_is_chosen()
    {
        var state = StateWith(new ResourceCatalog());

        Assert.Empty(state.ImageOffers("eastus", ""));
        Assert.Empty(state.ImageSkus("eastus", "MicrosoftWindowsServer", ""));
    }

    /// <summary>
    /// An image outside the built-in list must not be blocked - the lists are a convenience, and
    /// an enclave can carry images this tool has never heard of.
    /// </summary>
    [Fact]
    public void An_unknown_publisher_offers_nothing_rather_than_the_wrong_thing()
    {
        var state = StateWith(new ResourceCatalog());

        Assert.Empty(state.ImageOffers("eastus", "ContosoImages"));
        Assert.Empty(state.ImageSkus("eastus", "ContosoImages", "ContosoLinux"));
    }
}
