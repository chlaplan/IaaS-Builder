using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// What a new plan starts with, and which roles may be added more than once.
/// </summary>
public class DefaultPlanAndRoleLimitsTests
{
    /// <summary>
    /// A client used to be seeded disabled. That still put a Workstation card on the Servers page
    /// of every new lab, which reads as "a client is part of the build" - and a disabled server is
    /// easy to enable by accident while looking for something else.
    /// </summary>
    [Fact]
    public void A_new_plan_contains_only_the_domain_controller()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");

        var server = Assert.Single(plan.Servers);
        Assert.Equal(ServerRole.DomainController, server.Role);
        Assert.True(server.Enabled);
    }

    [Fact]
    public void No_client_is_in_a_new_plan_at_all()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");

        Assert.DoesNotContain(plan.Servers, s => s.Role == ServerRole.Workstation);
    }

    [Fact]
    public void A_client_can_still_be_added()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");

        var client = PlanFactory.AddServer(plan, ServerRole.Workstation);

        Assert.True(client.Enabled);
        Assert.Contains(plan.Servers, s => s.Role == ServerRole.Workstation);
    }

    /// <summary>
    /// One forest, one first domain controller. The validator already rejected a second one, but
    /// only when the plan was validated; refusing the click says so when it is asked for.
    /// </summary>
    [Fact]
    public void A_second_primary_domain_controller_is_refused()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");

        Assert.False(PlanFactory.CanAddServer(plan, ServerRole.DomainController, out var reason));
        Assert.Contains("Additional Domain Controller", reason);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => PlanFactory.AddServer(plan, ServerRole.DomainController));

        Assert.Contains("Additional Domain Controller", thrown.Message);
        Assert.Single(plan.Servers);
    }

    [Fact]
    public void Additional_domain_controllers_are_not_limited()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");

        PlanFactory.AddServer(plan, ServerRole.AdditionalDomainController);
        PlanFactory.AddServer(plan, ServerRole.AdditionalDomainController);
        PlanFactory.AddServer(plan, ServerRole.AdditionalDomainController);

        Assert.Equal(3, plan.Servers.Count(s => s.Role == ServerRole.AdditionalDomainController));
        Assert.True(PlanFactory.CanAddServer(plan, ServerRole.AdditionalDomainController, out _));

        // Distinct names and addresses, or ARM deploys one machine three times.
        var names = plan.Servers.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var addresses = plan.Servers.Select(s => s.PrivateIpAddress).ToList();
        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Only roles a forest can genuinely have one of are single-instance. Anything else marked as
    /// one would silently stop an operator building, say, a second member server.
    /// </summary>
    [Fact]
    public void Only_the_roles_a_forest_has_one_of_are_single_instance()
    {
        var singles = RoleCatalog.All.Where(r => r.SingleInstance).Select(r => r.Role).ToList();

        Assert.Equal(
            [ServerRole.DomainController, ServerRole.CertificateAuthority],
            singles.OrderBy(r => (int)r));
    }

    /// <summary>
    /// A plan that already contains two primary DCs - hand-edited, or written by an older build -
    /// still has to be rejected, because the refusal above only guards the add path.
    /// </summary>
    [Fact]
    public void A_plan_file_with_two_primary_domain_controllers_is_still_rejected()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.ResourceGroup = "rg-lab";
        plan.Azure.Location = "usgovvirginia";
        plan.Servers.Add(RoleCatalog.CreateDefault(ServerRole.DomainController, plan.Network.Prefix));

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var result = new DeploymentPlanValidator().Validate(plan, secrets);

        Assert.Contains(result.Issues, i =>
            i.Severity == ValidationSeverity.Error &&
            i.Message.Contains("first Domain Controller", StringComparison.Ordinal));
    }
}
