using System.Text.Json;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The virtual network has to be created from the plan's <em>address space</em>, not from the
/// workload subnet.
/// </summary>
/// <remarks>
/// <para>
/// Networking.json built the vnet's <c>addressSpace</c> from <c>subnetAddressPrefix</c>, so the
/// deployed vnet was exactly the workload subnet - 10.10.0.0/24 for the shipped defaults, rather
/// than the 10.10.0.0/16 shown on the Network page. The declared
/// <c>virtualNetworkAddressPrefix</c> was never referenced by any resource.
/// </para>
/// <para>
/// Two consequences, and the second is why this went unnoticed for so long. The "Address space"
/// field had no effect on the deployed network at all. And Azure Bastion could never deploy:
/// AzureBastionSubnet must sit inside the vnet without overlapping the workload subnet, but the
/// workload subnet <em>was</em> the whole vnet, so ARM answered every attempt with
/// <c>NetcfgSubnetRangeOutsideVnet</c>.
/// </para>
/// <para>
/// The validator checks the Bastion prefix against <c>network.addressPrefix</c>, which was the
/// right rule against the wrong template - validation passed and ARM then refused the subnet,
/// several minutes and one resource group later. These tests pin the template to the value the
/// validator reasons about, because the two disagreeing is silent in every other way.
/// </para>
/// </remarks>
public class VirtualNetworkAddressSpaceTests
{
    /// <summary>The binder key that carries <see cref="NetworkSpec.AddressPrefix"/>.</summary>
    private const string AddressSpaceParameter = "addressprefix";

    /// <summary>The binder key that carries <see cref="NetworkSpec.SubnetPrefix"/>.</summary>
    private const string SubnetParameter = "addresssubnet";

    [Fact]
    public void The_virtual_network_is_sized_to_the_address_space_not_the_workload_subnet()
    {
        var parameter = ResolveAddressSpaceParameter(TemplatePaths.Networking);

        Assert.Equal(AddressSpaceParameter, parameter);
    }

    /// <summary>
    /// The same defect, stated as the behaviour that actually failed: with nothing but the shipped
    /// defaults, the Bastion subnet must fall inside the address space the template will build.
    /// </summary>
    [Fact]
    public void The_default_bastion_subnet_fits_inside_the_network_the_template_will_build()
    {
        var plan = PlanFactory.CreateDefault();
        var parameter = ResolveAddressSpaceParameter(TemplatePaths.Networking);

        // Whatever the template reads, that is the vnet the Bastion subnet has to fit inside.
        var deployedVnet = parameter switch
        {
            AddressSpaceParameter => plan.Network.AddressPrefix,
            SubnetParameter => plan.Network.SubnetPrefix,
            _ => throw new Xunit.Sdk.XunitException(
                $"Networking.json builds the vnet from an unrecognised parameter '{parameter}'."),
        };

        Assert.True(Cidr.TryParse(deployedVnet, out var vnet), $"'{deployedVnet}' is not a CIDR.");
        Assert.True(
            Cidr.TryParse(plan.Bastion.SubnetPrefix, out var bastion),
            $"'{plan.Bastion.SubnetPrefix}' is not a CIDR.");

        Assert.True(
            vnet.Contains(bastion),
            $"The default Bastion subnet {bastion} is outside the virtual network {vnet} that "
                + $"Networking.json builds from '{parameter}'. ARM rejects this as "
                + "NetcfgSubnetRangeOutsideVnet after the vnet has already been created.");
    }

    /// <summary>
    /// The workload subnet must still be carved out of the address space rather than filling it,
    /// otherwise there is no room for the Bastion subnet even once the vnet is sized correctly.
    /// </summary>
    [Fact]
    public void The_workload_subnet_leaves_room_inside_the_address_space()
    {
        var plan = PlanFactory.CreateDefault();

        Assert.True(Cidr.TryParse(plan.Network.AddressPrefix, out var vnet));
        Assert.True(Cidr.TryParse(plan.Network.SubnetPrefix, out var workload));
        Assert.True(Cidr.TryParse(plan.Bastion.SubnetPrefix, out var bastion));

        Assert.True(vnet.Contains(workload), $"Workload subnet {workload} is outside {vnet}.");
        Assert.False(
            workload.Overlaps(bastion),
            $"The workload subnet {workload} overlaps AzureBastionSubnet {bastion}.");
    }

    /// <summary>
    /// Follows <c>addressSpace.addressPrefixes[0]</c> through the <c>networkSettings</c> variable
    /// to the ARM parameter it ultimately reads, so the assertion is about the value that reaches
    /// Azure rather than about the shape of the expression.
    /// </summary>
    private static string ResolveAddressSpaceParameter(string relativePath)
    {
        var path = Path.Combine(RepoRoot.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        var vnet = document.RootElement.GetProperty("resources")
            .EnumerateArray()
            .Single(r => r.TryGetProperty("type", out var t)
                && t.GetString() == "Microsoft.Network/virtualNetworks");

        var expression = vnet.GetProperty("properties")
            .GetProperty("addressSpace")
            .GetProperty("addressPrefixes")
            .EnumerateArray()
            .Single()
            .GetString()!;

        return ResolveExpression(expression, document.RootElement);
    }

    private static string ResolveExpression(string expression, JsonElement root)
    {
        var inner = expression.Trim();
        if (inner.StartsWith('[') && inner.EndsWith(']'))
        {
            inner = inner[1..^1];
        }

        // variables('networkSettings').someKey -> look the key up and resolve it in turn.
        const string VariablePrefix = "variables('";
        if (inner.StartsWith(VariablePrefix, StringComparison.Ordinal))
        {
            var close = inner.IndexOf("')", StringComparison.Ordinal);
            var variableName = inner[VariablePrefix.Length..close];
            var member = inner[(close + 2)..].TrimStart('.');

            var value = root.GetProperty("variables").GetProperty(variableName);
            if (member.Length > 0)
            {
                value = value.GetProperty(member);
            }

            return ResolveExpression(value.GetString()!, root);
        }

        const string ParameterPrefix = "parameters('";
        if (inner.StartsWith(ParameterPrefix, StringComparison.Ordinal))
        {
            return inner[ParameterPrefix.Length..inner.IndexOf("')", StringComparison.Ordinal)];
        }

        return inner;
    }
}
