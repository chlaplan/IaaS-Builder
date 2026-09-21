using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Serialization;

namespace IaaSBuilder.Core.Tests;

public class DeploymentPlanSerializerTests
{
    /// <summary>
    /// The legacy Save/Load pair used Export-Csv plus ~100 hand-written field mappings and
    /// silently dropped every combo box, every checkbox and the entire SACA tab. A
    /// round-trip test is the regression guard that could never have existed before.
    /// </summary>
    [Fact]
    public void Round_trip_preserves_the_entire_plan()
    {
        var original = PlanFactory.CreateDefault("contoso", "contoso.local");

        original.Hardening.ApplyStig = true;
        original.Hardening.ApplyMicrosoftBaseline = true;
        original.DedicatedHost = new DedicatedHostSpec
        {
            Enabled = true,
            HostGroupName = "hg1",
            Sku = "DSv3-Type1"
        };

        PlanFactory.AddServer(original, ServerRole.Sql);
        var sharePoint = PlanFactory.AddServer(original, ServerRole.SharePoint);
        sharePoint.SharePointVersion = "2019";
        sharePoint.ExtraParameters["customFlag"] = "yes";

        original.Avd = new AvdSpec
        {
            Enabled = true,
            HostPoolName = "contoso-hp",
            MetadataLocation = "eastus",
            SessionHostCount = 4
        };

        original.Saca = new SacaSpec
        {
            Enabled = true,
            Tier = 3,
            VNetName = "saca-vnet",
            DnsLabel = "saca",
            Subnets =
            {
                ["Management"] = new SacaSubnet { Name = "mgmt", AddressPrefix = "10.90.1.0/24" },
                ["External"] = new SacaSubnet { Name = "ext", AddressPrefix = "10.90.2.0/24" }
            },
            Appliances =
            {
                ["BigIP_VM1"] = new SacaAppliance
                {
                    Name = "bigip1",
                    VmSize = "Standard_D8s_v5",
                    Addresses = { ["ExternalPri"] = "10.90.2.11", ["Management"] = "10.90.1.11" }
                }
            },
            SharedAddresses = { ["SB_LB_IP"] = "10.90.1.200" }
        };

        var restored = DeploymentPlanSerializer.Deserialize(DeploymentPlanSerializer.Serialize(original));

        Assert.Equal(original.Servers.Count, restored.Servers.Count);
        Assert.Equal(original.Identity.DomainName, restored.Identity.DomainName);

        // Checkboxes - dropped entirely by the CSV round-trip.
        Assert.True(restored.Hardening.ApplyStig);
        Assert.True(restored.Hardening.ApplyMicrosoftBaseline);
        Assert.True(restored.Bastion.Enabled);

        // Combo box selections - also dropped.
        Assert.Equal(original.Servers[0].VmSize, restored.Servers[0].VmSize);
        Assert.Equal(original.Servers[0].Image.Sku, restored.Servers[0].Image.Sku);

        // Dedicated host.
        Assert.Equal("hg1", restored.DedicatedHost!.HostGroupName);

        // AVD.
        Assert.Equal(4, restored.Avd!.SessionHostCount);
        Assert.Equal("contoso-hp", restored.Avd.HostPoolName);

        // The entire SACA tab.
        Assert.Equal(3, restored.Saca!.Tier);
        Assert.Equal("10.90.2.0/24", restored.Saca.Subnets["External"].AddressPrefix);
        Assert.Equal("10.90.2.11", restored.Saca.Appliances["BigIP_VM1"].Addresses["ExternalPri"]);
        Assert.Equal("10.90.1.200", restored.Saca.SharedAddresses["SB_LB_IP"]);

        // Per-server escape hatch.
        Assert.Equal("yes", restored.Servers.Single(s => s.Role == ServerRole.SharePoint)
            .ExtraParameters["customFlag"]?.ToString());
    }

    [Fact]
    public void Serialized_plan_never_contains_a_password()
    {
        var plan = PlanFactory.CreateDefault();
        var json = DeploymentPlanSerializer.Serialize(plan);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_unknown_schema_version()
    {
        var json = DeploymentPlanSerializer.Serialize(PlanFactory.CreateDefault())
            .Replace($"\"{DeploymentPlan.CurrentSchemaVersion}\"", "\"0.1\"");

        var ex = Assert.Throws<InvalidDataException>(() => DeploymentPlanSerializer.Deserialize(json));
        Assert.Contains("0.1", ex.Message);
    }

    [Fact]
    public async Task Saves_and_loads_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iaas-plan-{Guid.NewGuid():N}.json");

        try
        {
            var plan = PlanFactory.CreateDefault("temp");
            await DeploymentPlanSerializer.SaveAsync(plan, path);

            var loaded = await DeploymentPlanSerializer.LoadAsync(path);
            Assert.Equal(plan.Network.Prefix, loaded.Network.Prefix);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class PlanFactoryTests
{
    [Fact]
    public void Default_plan_is_valid()
    {
        var plan = PlanFactory.CreateDefault();

        // 'init' deliberately leaves the subscription blank for the operator to fill in.
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var result = new Validation.DeploymentPlanValidator().Validate(plan, secrets);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AddServer_allocates_a_free_address_in_the_subnet()
    {
        var plan = PlanFactory.CreateDefault();
        var used = plan.Servers.Select(s => s.PrivateIpAddress).ToList();

        var added = PlanFactory.AddServer(plan, ServerRole.Sql);

        Assert.NotEmpty(added.PrivateIpAddress);
        Assert.DoesNotContain(added.PrivateIpAddress, used);
    }

    [Fact]
    public void AddServer_never_allocates_an_azure_reserved_address()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Servers.Clear();

        var added = PlanFactory.AddServer(plan, ServerRole.DomainController);

        Assert.True(Validation.Cidr.TryParse(plan.Network.SubnetPrefix, out var subnet));
        Assert.False(subnet.IsAzureReserved(System.Net.IPAddress.Parse(added.PrivateIpAddress)));
    }

    [Fact]
    public void AddServer_deduplicates_computer_names()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Servers.Clear();

        var first = PlanFactory.AddServer(plan, ServerRole.Sql);
        var second = PlanFactory.AddServer(plan, ServerRole.Sql);

        Assert.NotEqual(first.Name, second.Name);
        Assert.True(Validation.AzureNaming.IsValidComputerName(second.Name));
    }
}
