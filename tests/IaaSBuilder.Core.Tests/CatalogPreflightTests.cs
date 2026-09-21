using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The catalog checks exist to catch the failures ARM template validation does not: a
/// marketplace image reference is only resolved when the VM is created, so a bad SKU otherwise
/// surfaces part way through a deployment. These tests pin the two behaviours that matter -
/// it must catch a genuinely wrong value, and it must stay silent when it has no evidence.
/// </summary>
public class CatalogPreflightTests
{
    private const string Location = "eastus";

    private static DeploymentPlan PlanWith(string vmSize, string publisher, string offer, string sku)
    {
        var plan = new DeploymentPlan();
        plan.Azure.Location = Location;
        plan.Servers.Add(new ServerSpec
        {
            Name = "dc01",
            Enabled = true,
            Role = ServerRole.DomainController,
            PrivateIpAddress = "10.0.1.4",
            VmSize = vmSize,
            Image = new ImageReferenceSpec { Publisher = publisher, Offer = offer, Sku = sku }
        });

        return plan;
    }

    private static ResourceCatalog FreshCatalog()
    {
        var catalog = new ResourceCatalog { CapturedUtc = DateTimeOffset.UtcNow };
        catalog.Locations.Add(new LocationInfo(Location, "East US", []));
        catalog.VmSizesByLocation[Location] = [new VmSizeInfo("Standard_D2s_v5", 2, 8192, 4)];
        catalog.SetImageSkus(Location, "MicrosoftWindowsServer", "WindowsServer",
            ["2019-Datacenter", "2022-datacenter-azure-edition"]);

        return catalog;
    }

    [Fact]
    public void A_null_catalog_produces_no_issues()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter");

        Assert.Empty(CatalogPreflight.Check(plan, null));
    }

    [Fact]
    public void An_empty_catalog_produces_no_issues()
    {
        var plan = PlanWith("Whatever_Size", "Nobody", "Nothing", "NoSuchSku");

        Assert.Empty(CatalogPreflight.Check(plan, new ResourceCatalog()));
    }

    [Fact]
    public void A_plan_matching_the_catalog_produces_no_issues()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter");

        Assert.Empty(CatalogPreflight.Check(plan, FreshCatalog()));
    }

    [Fact]
    public void An_unknown_image_sku_is_an_error()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "2022-datacenter-does-not-exist");

        var issue = Assert.Single(CatalogPreflight.Check(plan, FreshCatalog()));

        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal("servers[0].image.sku", issue.Path);
        Assert.Contains("2022-datacenter-does-not-exist", issue.Message);
    }

    [Fact]
    public void An_unknown_vm_size_is_an_error()
    {
        var plan = PlanWith("Standard_NOPE", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter");

        var issue = Assert.Single(CatalogPreflight.Check(plan, FreshCatalog()));

        Assert.Equal("servers[0].vmSize", issue.Path);
        Assert.Contains(Location, issue.Message);
    }

    [Fact]
    public void An_unknown_region_is_an_error()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter");
        plan.Azure.Location = "mars-central";

        // The size and SKU lists are indexed by region, so only the region itself is reported;
        // flagging every server as well would be noise from a single root cause.
        var issue = Assert.Single(CatalogPreflight.Check(plan, FreshCatalog()));

        Assert.Equal("azure.location", issue.Path);
    }

    [Fact]
    public void An_offer_that_was_never_enumerated_is_not_flagged()
    {
        // We only enumerate a fixed set of publisher/offer pairs, so absence is not evidence.
        var plan = PlanWith("Standard_D2s_v5", "SomeIsvPublisher", "TheirAppliance", "v1");

        Assert.Empty(CatalogPreflight.Check(plan, FreshCatalog()));
    }

    [Fact]
    public void A_disabled_server_is_not_checked()
    {
        var plan = PlanWith("Standard_NOPE", "MicrosoftWindowsServer", "WindowsServer", "NoSuchSku");
        plan.Servers[0].Enabled = false;

        Assert.Empty(CatalogPreflight.Check(plan, FreshCatalog()));
    }

    [Fact]
    public void A_stale_snapshot_only_warns()
    {
        var catalog = FreshCatalog();
        catalog.CapturedUtc = DateTimeOffset.UtcNow - CatalogPreflight.StaleAfter - TimeSpan.FromDays(1);

        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "some-brand-new-sku");

        var issue = Assert.Single(CatalogPreflight.Check(plan, catalog));

        // A SKU released after the snapshot was taken must not block a deployment.
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void A_missing_region_defers_to_the_plan_validator()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter");
        plan.Azure.Location = "";

        Assert.Empty(CatalogPreflight.Check(plan, FreshCatalog()));
    }

    [Fact]
    public void The_message_lists_the_available_skus()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "wrong");

        var issue = Assert.Single(CatalogPreflight.Check(plan, FreshCatalog()));

        Assert.Contains("2019-Datacenter", issue.Message);
        Assert.Contains("2022-datacenter-azure-edition", issue.Message);
    }

    [Fact]
    public void Server_index_refers_to_the_position_in_the_plan_not_among_enabled_servers()
    {
        var plan = PlanWith("Standard_D2s_v5", "MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter");
        plan.Servers[0].Enabled = false;
        plan.Servers.Add(new ServerSpec
        {
            Name = "app01",
            Enabled = true,
            Role = ServerRole.MemberServer,
            PrivateIpAddress = "10.0.1.5",
            VmSize = "Standard_D2s_v5",
            Image = new ImageReferenceSpec
            {
                Publisher = "MicrosoftWindowsServer",
                Offer = "WindowsServer",
                Sku = "not-a-real-sku"
            }
        });

        var issue = Assert.Single(CatalogPreflight.Check(plan, FreshCatalog()));

        // The UI indexes plan.Servers, so an index of 0 here would point at the wrong row.
        Assert.Equal("servers[1].image.sku", issue.Path);
    }
}
