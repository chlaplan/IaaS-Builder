using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

public class SubnetLayoutTests
{
    private static DeploymentPlan Plan()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Bastion.Enabled = true;
        return plan;
    }

    /// <summary>
    /// Repairing a plan should land somewhere familiar, not merely somewhere valid. A /16 has to
    /// reproduce the shipped defaults exactly, or the button silently reorganises the network of
    /// anyone who presses it out of curiosity.
    /// </summary>
    [Fact]
    public void A_slash_16_reproduces_the_shipped_defaults()
    {
        Assert.True(Cidr.TryParse("10.10.0.0/16", out var vnet));
        Assert.True(SubnetLayout.TryCompute(vnet, out var workload, out var bastion));

        Assert.Equal("10.10.0.0/24", workload);
        Assert.Equal("10.10.1.0/26", bastion);
    }

    [Theory]
    [InlineData("10.44.0.0/16", "10.44.0.0/24", "10.44.1.0/26")]
    [InlineData("172.16.0.0/16", "172.16.0.0/24", "172.16.1.0/26")]
    [InlineData("192.168.5.0/24", "192.168.5.0/25", "192.168.5.128/26")]
    [InlineData("10.0.0.0/25", "10.0.0.0/26", "10.0.0.64/26")]
    public void Computes_two_non_overlapping_subnets_inside_the_space(
        string space, string expectedWorkload, string expectedBastion)
    {
        Assert.True(Cidr.TryParse(space, out var vnet));
        Assert.True(SubnetLayout.TryCompute(vnet, out var workload, out var bastion));

        Assert.Equal(expectedWorkload, workload);
        Assert.Equal(expectedBastion, bastion);

        // The properties that actually matter, asserted independently of the expected strings -
        // otherwise this only checks that the arithmetic matches itself.
        Assert.True(Cidr.TryParse(workload, out var w));
        Assert.True(Cidr.TryParse(bastion, out var b));
        Assert.True(vnet.Contains(w), $"{workload} is not inside {space}");
        Assert.True(vnet.Contains(b), $"{bastion} is not inside {space}");
        Assert.False(w.Overlaps(b), $"{workload} overlaps {bastion}");
        Assert.True(b.PrefixLength <= 26, "AzureBastionSubnet must be /26 or larger");
    }

    /// <summary>
    /// A /26 address space cannot hold a workload subnet and a /26 Bastion subnet. Saying so is a
    /// real answer; emitting something ARM will reject later is not.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/26")]
    [InlineData("10.0.0.0/27")]
    [InlineData("10.0.0.0/30")]
    public void Refuses_an_address_space_too_small_to_hold_both(string space)
    {
        Assert.True(Cidr.TryParse(space, out var vnet));
        Assert.False(SubnetLayout.TryCompute(vnet, out _, out _));
    }

    /// <summary>The deployment failure this was written for.</summary>
    [Fact]
    public void Detects_and_repairs_a_bastion_subnet_left_behind_by_a_moved_address_space()
    {
        var plan = Plan();
        plan.Network.AddressPrefix = "10.44.0.0/16";
        plan.Network.SubnetPrefix = "10.44.0.0/24";
        // Bastion still on the old 10.10.x range - exactly what ARM rejected.

        Assert.True(SubnetLayout.NeedsRefit(plan));

        var before = new DeploymentPlanValidator().Validate(plan);
        Assert.Contains(before.Errors, e => e.Path == "bastion.subnetPrefix");

        Assert.True(SubnetLayout.TryRefit(plan));

        Assert.False(SubnetLayout.NeedsRefit(plan));
        var after = new DeploymentPlanValidator().Validate(plan);
        Assert.DoesNotContain(after.Errors, e => e.Path == "bastion.subnetPrefix");
        Assert.DoesNotContain(after.Errors, e => e.Path == "network.subnetPrefix");
    }

    [Fact]
    public void A_healthy_plan_does_not_ask_to_be_refitted()
    {
        Assert.False(SubnetLayout.NeedsRefit(Plan()));
    }

    /// <summary>
    /// Bastion is only validated and only repaired when it is actually being deployed, so a stale
    /// prefix on a disabled Bastion must not demand attention.
    /// </summary>
    [Fact]
    public void A_stale_prefix_on_a_disabled_bastion_is_left_alone()
    {
        var plan = Plan();
        plan.Bastion.Enabled = false;
        plan.Bastion.SubnetPrefix = "10.99.9.0/26";
        plan.Network.AddressPrefix = "10.44.0.0/16";
        plan.Network.SubnetPrefix = "10.44.0.0/24";

        Assert.False(SubnetLayout.NeedsRefit(plan));

        plan.Network.SubnetPrefix = "10.10.0.0/24";
        Assert.True(SubnetLayout.NeedsRefit(plan));
        Assert.True(SubnetLayout.TryRefit(plan));

        Assert.Equal("10.99.9.0/26", plan.Bastion.SubnetPrefix);
    }

    [Fact]
    public void An_overlapping_bastion_subnet_is_also_a_refit()
    {
        var plan = Plan();
        plan.Bastion.SubnetPrefix = plan.Network.SubnetPrefix;

        Assert.True(SubnetLayout.NeedsRefit(plan));
        Assert.True(SubnetLayout.TryRefit(plan));
        Assert.False(SubnetLayout.NeedsRefit(plan));
    }

    [Fact]
    public void Cidr_rejects_an_address_that_is_not_on_a_boundary()
    {
        // 10.0.0.64 is not the start of a /25. Rounding it down would hand back a range the
        // caller never asked for.
        Assert.False(Cidr.TryCreate(0x0A000040, 25, out _));
        Assert.True(Cidr.TryCreate(0x0A000000, 25, out _));
    }
}
