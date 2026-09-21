using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

public class DeploymentPlanValidatorTests
{
    private static DeploymentPlan Plan()
    {
        var plan = PlanFactory.CreateDefault("contoso", "contoso.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        return plan;
    }

    private static ValidationResult Validate(DeploymentPlan plan, string password = "Sup3rSecret!Lab")
    {
        using var secrets = new DeploymentSecrets(password);
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }

    private static void AssertError(ValidationResult result, string pathFragment, string messageFragment) =>
        Assert.Contains(result.Errors, e =>
            e.Path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_good_plan_produces_no_errors() =>
        Assert.True(Validate(Plan()).IsValid);

    [Fact]
    public void Rejects_an_ip_outside_the_subnet()
    {
        var plan = Plan();
        plan.Servers[0].PrivateIpAddress = "192.168.50.10";

        AssertError(Validate(plan), "privateIpAddress", "outside the workload subnet");
    }

    [Fact]
    public void Rejects_azure_reserved_addresses()
    {
        var plan = Plan();
        plan.Servers[0].PrivateIpAddress = "10.10.0.1";

        AssertError(Validate(plan), "privateIpAddress", "reserved by Azure");
    }

    [Fact]
    public void Rejects_duplicate_ip_addresses()
    {
        var plan = Plan();
        var second = PlanFactory.AddServer(plan, ServerRole.Sql);
        second.PrivateIpAddress = plan.Servers[0].PrivateIpAddress;

        AssertError(Validate(plan), "privateIpAddress", "Duplicate IP");
    }

    [Fact]
    public void Rejects_duplicate_computer_names()
    {
        var plan = Plan();
        var second = PlanFactory.AddServer(plan, ServerRole.Sql);
        second.Name = plan.Servers[0].Name;

        AssertError(Validate(plan), "name", "Duplicate computer name");
    }

    [Fact]
    public void Rejects_computer_names_longer_than_fifteen_characters()
    {
        var plan = Plan();
        plan.Servers[0].Name = "this-name-is-far-too-long";

        AssertError(Validate(plan), "name", "valid Windows computer name");
    }

    [Fact]
    public void Rejects_a_subnet_outside_the_virtual_network()
    {
        var plan = Plan();
        plan.Network.SubnetPrefix = "172.16.0.0/24";

        AssertError(Validate(plan), "subnetPrefix", "not inside virtual network");
    }

    [Fact]
    public void Rejects_a_bastion_subnet_that_overlaps_the_workload_subnet()
    {
        var plan = Plan();
        plan.Bastion.SubnetPrefix = "10.10.0.0/26";

        AssertError(Validate(plan), "bastion", "overlaps");
    }

    [Fact]
    public void Rejects_a_bastion_subnet_smaller_than_a_slash_26()
    {
        var plan = Plan();
        plan.Bastion.SubnetPrefix = "10.10.1.0/28";

        AssertError(Validate(plan), "bastion", "/26 or larger");
    }

    /// <summary>
    /// The failure a real deployment hit: the address space was moved to a different range and the
    /// workload subnet moved with it, but the Bastion prefix was left on the old one. The vnet
    /// deploys happily - it is the Bastion subnet inside it that ARM rejects, several minutes and
    /// one resource group later, with NetcfgSubnetRangeOutsideVnet.
    /// </summary>
    [Fact]
    public void Rejects_a_bastion_subnet_left_behind_when_the_address_space_moves()
    {
        var plan = Plan();
        plan.Network.AddressPrefix = "10.44.0.0/16";
        plan.Network.SubnetPrefix = "10.44.0.0/24";
        // Bastion still on the old 10.10.x range.

        AssertError(Validate(plan), "bastion", "not inside virtual network");
    }

    [Fact]
    public void Rejects_an_invalid_storage_account_name()
    {
        var plan = Plan();
        plan.Artifacts.UsePublicPackageUrl = false;
        plan.Artifacts.StorageAccountName = "Not-A-Valid-Name";

        AssertError(Validate(plan), "storageAccountName", "lowercase");
    }

    [Fact]
    public void Rejects_a_non_qualified_domain_name()
    {
        var plan = Plan();
        plan.Identity.DomainName = "contoso";

        AssertError(Validate(plan), "domainName", "fully qualified");
    }

    [Fact]
    public void Rejects_azure_reserved_administrator_names()
    {
        var plan = Plan();
        plan.Identity.AdminUsername = "administrator";

        AssertError(Validate(plan), "adminUsername", "reserved");
    }

    [Fact]
    public void Rejects_a_weak_password() =>
        AssertError(Validate(Plan(), "password"), "adminPassword", "3 of");

    [Fact]
    public void Rejects_a_role_whose_prerequisite_is_not_in_the_plan()
    {
        var plan = Plan();
        plan.Servers.RemoveAll(s => s.Role == ServerRole.DomainController);

        var sql = PlanFactory.AddServer(plan, ServerRole.Sql);
        Assert.NotNull(sql);

        AssertError(Validate(plan), "role", "depends on");
    }

    [Fact]
    public void Rejects_more_than_one_primary_domain_controller()
    {
        var plan = Plan();

        // PlanFactory.AddServer refuses this now, but a hand-edited or older plan file can still
        // contain two, and the validator is the thing that has to catch those.
        plan.Servers.Add(IaaSBuilder.Core.Roles.RoleCatalog.CreateDefault(ServerRole.DomainController, plan.Network.Prefix));

        AssertError(Validate(plan), "servers", "first Domain Controller");
    }

    [Fact]
    public void Rejects_avd_without_a_domain_controller()
    {
        var plan = Plan();
        plan.Servers.RemoveAll(s => s.Role == ServerRole.DomainController);
        plan.Avd = new AvdSpec { Enabled = true, HostPoolName = "hp", MetadataLocation = "eastus" };

        AssertError(Validate(plan), "avd", "Domain Controller must be enabled");
    }

    [Fact]
    public void Rejects_avd_without_a_metadata_region()
    {
        var plan = Plan();
        plan.Avd = new AvdSpec { Enabled = true, HostPoolName = "hp", MetadataLocation = "" };

        AssertError(Validate(plan), "metadataLocation", "metadata region is required");
    }

    [Fact]
    public void Rejects_overlapping_saca_subnets()
    {
        var plan = Plan();
        plan.Saca = new SacaSpec
        {
            Enabled = true,
            Tier = 3,
            VNetName = "saca-vnet",
            DnsLabel = "saca",
            Subnets =
            {
                ["Management"] = new SacaSubnet { Name = "mgmt", AddressPrefix = "10.90.0.0/16" },
                ["External"] = new SacaSubnet { Name = "ext", AddressPrefix = "10.90.1.0/24" }
            }
        };

        AssertError(Validate(plan), "saca.subnets", "overlaps");
    }

    [Fact]
    public void Warns_about_a_domain_controller_on_a_spot_instance()
    {
        var plan = Plan();
        plan.Servers[0].UseSpotInstance = true;

        var result = Validate(plan);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Message.Contains("evicted"));
    }

    [Fact]
    public void Requires_a_prestaged_location_when_upload_is_disabled()
    {
        var plan = Plan();
        plan.Artifacts.SkipUpload = true;
        plan.Artifacts.ArtifactsLocationOverride = null;

        AssertError(Validate(plan), "artifactsLocationOverride", "pre-staged");
    }

    [Fact]
    public void Accepts_a_prestaged_location_when_upload_is_disabled()
    {
        var plan = Plan();
        plan.Artifacts.SkipUpload = true;
        plan.Artifacts.StorageAccountName = "INVALID";   // must now be ignored
        plan.Artifacts.ArtifactsLocationOverride = "https://prestaged.invalid/dsc/";

        Assert.True(Validate(plan).IsValid);
    }

    [Fact]
    public void Reports_a_missing_dsc_configuration_for_a_role()
    {
        var plan = Plan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        // A package that contains no configuration at all for the DC role.
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "JoinDomain" };
        var result = new DeploymentPlanValidator(tokens).Validate(plan, secrets);

        AssertError(result, "role", "not present in the DSC package");
    }
}
