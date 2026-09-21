using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// SACA is hidden in the UI but deliberately left deployable, so a plan file authored earlier
/// still works. That makes these warnings the only thing standing between an operator and a
/// deployment that fails on image resolution: the images are hard-coded inside the ARM templates
/// rather than bound from the plan, so <see cref="CatalogPreflight"/> cannot see them.
/// </summary>
public class SacaLegacyTests
{
    private static DeploymentPlan Plan()
    {
        var plan = PlanFactory.CreateDefault("contoso", "contoso.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        return plan;
    }

    private static ValidationResult Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }

    private static SacaSpec Spec(int tier) => new()
    {
        Enabled = true,
        Tier = tier,
        VNetName = "saca-vnet",
        DnsLabel = "saca"
    };

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Warns_that_the_pinned_bigip_image_is_gone(int tier)
    {
        var plan = Plan();
        plan.Saca = Spec(tier);

        var warning = Assert.Single(Validate(plan).Warnings, w => w.Path == "saca");
        Assert.Contains("14.1.200000", warning.Message);
        Assert.Contains("missionlz", warning.Message);
    }

    /// <summary>Ubuntu 18.04 only appears in the 3-tier IPS pair, so only 3-tier should say so.</summary>
    [Fact]
    public void Mentions_the_dead_ubuntu_image_only_for_three_tier()
    {
        var oneTier = Plan();
        oneTier.Saca = Spec(1);
        var three = Plan();
        three.Saca = Spec(3);

        Assert.DoesNotContain("18.04", Validate(oneTier).Warnings.Single(w => w.Path == "saca").Message);
        Assert.Contains("18.04", Validate(three).Warnings.Single(w => w.Path == "saca").Message);
    }

    /// <summary>
    /// The trap this guards: ArmTemplate.Bind drops parameters the template does not declare, so a
    /// mistyped appliance key leaves BigIP_VM1_Size at its template default of "f5dnst3-bigip0" -
    /// a host name passed to ARM as a VM size.
    /// </summary>
    [Fact]
    public void Warns_about_appliance_keys_the_template_expects_but_the_plan_omits()
    {
        var plan = Plan();
        plan.Saca = Spec(1);
        plan.Saca.Appliances["BigIP_VM1"] = new SacaAppliance { Name = "bigip0", VmSize = "Standard_D8s_v4" };
        plan.Saca.Appliances["Bigip_vm2_typo"] = new SacaAppliance { Name = "bigip1", VmSize = "Standard_D8s_v4" };

        var paths = Validate(plan).Warnings.Select(w => w.Path).ToList();

        Assert.DoesNotContain("saca.appliances[BigIP_VM1]", paths);
        Assert.Contains("saca.appliances[BigIP_VM2]", paths);
        Assert.Contains("saca.appliances[SB_LB]", paths);
        Assert.Contains("saca.appliances[NB_LB]", paths);
    }

    [Fact]
    public void Three_tier_expects_the_larger_appliance_set()
    {
        var plan = Plan();
        plan.Saca = Spec(3);

        var paths = Validate(plan).Warnings.Select(w => w.Path).ToList();

        foreach (var key in (string[])["BigIP_VM3", "BigIP_VM4", "IPS_FW0", "IPS_FW1"])
        {
            Assert.Contains($"saca.appliances[{key}]", paths);
        }
    }

    /// <summary>
    /// Warnings, not errors. The whole point of leaving SACA deployable is that somebody with a
    /// working pre-staged environment is not blocked by our opinion of it.
    /// </summary>
    [Fact]
    public void The_legacy_warnings_do_not_make_a_plan_invalid()
    {
        var plan = Plan();
        plan.Saca = Spec(1);

        var result = Validate(plan);

        Assert.NotEmpty(result.Warnings);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void A_plan_without_saca_says_nothing_about_it()
    {
        var plan = Plan();
        plan.Saca = null;

        Assert.DoesNotContain(Validate(plan).Issues, i => i.Path.StartsWith("saca", StringComparison.Ordinal));
    }
}
