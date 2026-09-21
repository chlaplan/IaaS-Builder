using IaaSBuilder.Core.Catalog;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// A refresh driven from the UI only asks Azure about the region being deployed to, because
/// sweeping every region costs hundreds of round trips. That makes the merge back into the
/// on-disk snapshot load-bearing: get it wrong and an air-gapped catalog is silently reduced
/// to one region, which only surfaces once someone is inside the enclave with no way to refresh.
/// </summary>
public class CatalogRefreshScopeTests
{
    private static ResourceCatalog CatalogWith(params (string Location, string Publisher, string Offer, string Sku)[] entries)
    {
        var catalog = new ResourceCatalog();
        foreach (var e in entries)
        {
            catalog.SetImageSkus(e.Location, e.Publisher, e.Offer, [e.Sku]);
        }

        return catalog;
    }

    [Fact]
    public void Regions_that_were_not_refreshed_are_preserved()
    {
        var current = CatalogWith(("eastus", "MicrosoftWindowsServer", "WindowsServer", "2022-datacenter-azure-edition"));
        var previous = CatalogWith(
            ("eastus", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter"),
            ("usgovvirginia", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter"),
            ("westeurope", "MicrosoftWindowsDesktop", "Windows-11", "win11-24h2-ent"));

        AzureCatalogSource.MergeUnrefreshedImageSkus(current, previous, new(StringComparer.OrdinalIgnoreCase) { "eastus" });

        Assert.Equal(["2019-Datacenter"], current.GetImageSkus("usgovvirginia", "MicrosoftWindowsServer", "WindowsServer"));
        Assert.Equal(["win11-24h2-ent"], current.GetImageSkus("westeurope", "MicrosoftWindowsDesktop", "Windows-11"));
    }

    [Fact]
    public void The_refreshed_region_wins_over_the_snapshot()
    {
        var current = CatalogWith(("eastus", "MicrosoftWindowsServer", "WindowsServer", "2022-datacenter-azure-edition"));
        var previous = CatalogWith(("eastus", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter"));

        AzureCatalogSource.MergeUnrefreshedImageSkus(current, previous, new(StringComparer.OrdinalIgnoreCase) { "eastus" });

        Assert.Equal(["2022-datacenter-azure-edition"], current.GetImageSkus("eastus", "MicrosoftWindowsServer", "WindowsServer"));
    }

    [Fact]
    public void An_offer_withdrawn_from_a_refreshed_region_is_not_resurrected()
    {
        // Azure was asked about eastus and did not return this offer. That absence is real
        // information, so carrying the stale entry forward would keep offering a dead SKU.
        var current = new ResourceCatalog();
        var previous = CatalogWith(("eastus", "MicrosoftSQLServer", "sql2019-ws2022", "standard"));

        AzureCatalogSource.MergeUnrefreshedImageSkus(current, previous, new(StringComparer.OrdinalIgnoreCase) { "eastus" });

        Assert.Empty(current.GetImageSkus("eastus", "MicrosoftSQLServer", "sql2019-ws2022"));
    }

    [Fact]
    public void Region_matching_ignores_case()
    {
        var current = new ResourceCatalog();
        var previous = CatalogWith(("EastUS", "MicrosoftSQLServer", "sql2019-ws2022", "standard"));

        AzureCatalogSource.MergeUnrefreshedImageSkus(current, previous, new(StringComparer.OrdinalIgnoreCase) { "eastus" });

        Assert.Empty(current.ImageSkus);
    }
}
