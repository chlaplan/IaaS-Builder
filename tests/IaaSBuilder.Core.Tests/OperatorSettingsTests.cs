using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;
using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Blank-until-saved behaviour for the fields that are genuine choices.
/// </summary>
public class OperatorSettingsTests
{
    /// <summary>
    /// The fields the checklist asks the operator to choose. If a step is added that asks for
    /// something new, <see cref="Every_field_the_checklist_asks_about_starts_blank"/> fails until
    /// it is either blanked here or deliberately left as an engineering default.
    /// </summary>
    private static DeploymentPlan Blanked()
    {
        var plan = PlanFactory.CreateDefault();
        OperatorSettings.ClearChoices(plan);
        return plan;
    }

    [Fact]
    public void Choices_start_blank()
    {
        var plan = Blanked();

        Assert.Equal("", plan.Azure.SubscriptionId);
        Assert.Equal("", plan.Azure.Location);
        Assert.Equal("", plan.Azure.ResourceGroup);
        Assert.Equal("", plan.Identity.DomainName);
        Assert.Equal("", plan.Identity.AdminUsername);
        Assert.True(string.IsNullOrEmpty(plan.Azure.TenantId));
    }

    [Fact]
    public void Engineering_defaults_are_left_alone()
    {
        // A blank CIDR or VM size helps nobody - those are defaults in the real sense, values
        // that are right until there is a reason to change them. Only choices are cleared.
        var plan = Blanked();

        Assert.False(string.IsNullOrWhiteSpace(plan.Network.AddressPrefix));
        Assert.False(string.IsNullOrWhiteSpace(plan.Network.SubnetPrefix));
        Assert.False(string.IsNullOrWhiteSpace(plan.Bastion.SubnetPrefix));
        Assert.False(string.IsNullOrWhiteSpace(plan.Artifacts.StorageAccountName));
        Assert.NotEmpty(plan.Servers);
        Assert.All(plan.Servers, s => Assert.False(string.IsNullOrWhiteSpace(s.VmSize)));
        Assert.All(plan.Servers, s => Assert.False(string.IsNullOrWhiteSpace(s.Image.Sku)));
    }

    [Fact]
    public void Every_field_the_checklist_asks_about_starts_blank()
    {
        // The checklist and the blanking have to agree. If a choice stays prepopulated, its step
        // ticks itself Done before the operator has decided anything, and the checklist silently
        // skips past it - the exact dishonesty this change exists to remove.
        var steps = SetupChecklist.For(Blanked(), signedIn: true, adminPassword: null);

        var choiceAnchors = new[] { "subscription", "region", "resourcegroup", "identity", "password" };

        foreach (var anchor in choiceAnchors)
        {
            var step = Assert.Single(steps, s => s.Anchor == anchor);
            Assert.False(step.Done, $"Step '{step.Title}' reports itself done on a blank plan.");
        }
    }

    [Fact]
    public void The_cli_default_plan_is_untouched()
    {
        // ClearChoices is a web-layer decision. `iaasbuilder init` and a large number of tests
        // rely on PlanFactory producing a plan that validates, and a headless init that emitted
        // an incomplete plan would be a regression.
        var plan = PlanFactory.CreateDefault();

        Assert.Equal("eastus", plan.Azure.Location);
        Assert.Equal("lab-rg", plan.Azure.ResourceGroup);
        Assert.Equal("contoso.local", plan.Identity.DomainName);
        Assert.Equal("labadmin", plan.Identity.AdminUsername);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var source = PlanFactory.CreateDefault();
        source.Azure.Cloud = AzureCloud.UsGovernment;
        source.Azure.SubscriptionId = "11111111-2222-3333-4444-555555555555";
        source.Azure.TenantId = "66666666-7777-8888-9999-000000000000";
        source.Azure.Location = "usgovvirginia";

        var restored = OperatorSettings.FromJson(OperatorSettings.Capture(source).ToJson());
        Assert.NotNull(restored);

        var target = Blanked();
        restored.ApplyTo(target);

        Assert.Equal(AzureCloud.UsGovernment, target.Azure.Cloud);
        Assert.Equal(source.Azure.SubscriptionId, target.Azure.SubscriptionId);
        Assert.Equal(source.Azure.TenantId, target.Azure.TenantId);
        Assert.Equal("usgovvirginia", target.Azure.Location);
        Assert.Equal(source.Azure.ResourceGroup, target.Azure.ResourceGroup);
        Assert.Equal(source.Identity.DomainName, target.Identity.DomainName);
        Assert.Equal(source.Identity.AdminUsername, target.Identity.AdminUsername);
    }

    [Fact]
    public void The_cloud_is_remembered_even_when_it_is_the_default()
    {
        // Cloud is applied unconditionally rather than "only when set", because Public is a
        // legitimate saved choice and is also the enum's zero value. Treating it as "unset" would
        // mean a US Government operator who switched back to Public could never save that.
        var source = Blanked();
        source.Azure.Cloud = AzureCloud.Public;

        var target = Blanked();
        target.Azure.Cloud = AzureCloud.UsGovernment;

        OperatorSettings.Capture(source).ApplyTo(target);

        Assert.Equal(AzureCloud.Public, target.Azure.Cloud);
    }

    [Fact]
    public void A_saved_blob_missing_newer_fields_does_not_wipe_them()
    {
        var target = Blanked();
        target.Azure.Location = "eastus2";

        var partial = OperatorSettings.FromJson("""{"cloud":"Public","subscriptionId":"abc"}""");
        Assert.NotNull(partial);
        partial.ApplyTo(target);

        Assert.Equal("abc", target.Azure.SubscriptionId);
        Assert.Equal("eastus2", target.Azure.Location);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"cloud\":")]
    public void Unreadable_storage_is_ignored_rather_than_thrown(string? json)
    {
        // localStorage is hand-editable and survives across builds. A parse failure here runs
        // during the first render of every page, so throwing would take the whole tool down.
        Assert.Null(OperatorSettings.FromJson(json));
    }

    [Fact]
    public void The_administrator_password_is_never_captured()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Identity.AdminUsername = "labops";

        var json = OperatorSettings.Capture(plan).ToJson();

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }
}
