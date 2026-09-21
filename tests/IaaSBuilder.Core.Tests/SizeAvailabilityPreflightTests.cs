using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The preflight check for VM sizes the subscription is not allowed to deploy.
/// </summary>
/// <remarks>
/// <para>
/// A deployment failed with <c>SkuNotAvailable</c> after the resource group and virtual network
/// had already been created. It was not a quota problem - the subscription had cores to spare -
/// the size was simply not offered to that subscription in that region, and Azure's own advice was
/// "try another size" without naming one that would work.
/// </para>
/// <para>
/// Restriction is a different axis from quota, so it gets its own check: a plan can be
/// simultaneously within quota and using a forbidden size, and reporting only one of the two would
/// send the operator round the loop twice.
/// </para>
/// </remarks>
public class SizeAvailabilityPreflightTests
{
    private const string Location = "usgovvirginia";

    private static DeploymentPlan PlanWith(params string[] vmSizes)
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.ResourceGroup = "rg-lab";
        plan.Azure.Location = Location;
        plan.Servers.Clear();

        foreach (var size in vmSizes)
        {
            var server = RoleCatalog.CreateDefault(ServerRole.MemberServer, plan.Network.Prefix);
            server.Name = "srv" + plan.Servers.Count;
            server.VmSize = size;
            plan.Servers.Add(server);
        }

        return plan;
    }

    private static PreflightFacts Facts(IReadOnlySet<string>? restricted) =>
        new(null, [], new Dictionary<string, string>(), null, "the subscription",
            null, null, null, restricted);

    private static IReadOnlySet<string> Restricted(params string[] sizes) =>
        new HashSet<string>(sizes, StringComparer.OrdinalIgnoreCase);

    private static PreflightIssue? SizeIssue(DeploymentPlan plan, PreflightFacts facts) =>
        DeploymentPreflight.Check(plan, facts)
            .FirstOrDefault(i => i.Title.Contains("not available", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_restricted_size_blocks_the_deployment()
    {
        var plan = PlanWith("Standard_D1");
        var issue = SizeIssue(plan, Facts(Restricted("Standard_D1")));

        Assert.NotNull(issue);
        Assert.Equal(PreflightSeverity.Blocking, issue.Severity);
        Assert.Contains("Standard_D1", issue.Detail);
        Assert.Contains(Location, issue.Title);
    }

    /// <summary>
    /// More quota does not fix a restriction, and an operator who has just been told about a quota
    /// shortfall will reasonably assume it does. The message has to say so.
    /// </summary>
    [Fact]
    public void The_message_says_this_is_not_a_quota_problem()
    {
        var issue = SizeIssue(PlanWith("Standard_D1"), Facts(Restricted("Standard_D1")));

        Assert.NotNull(issue);
        Assert.Contains("quota", issue.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_available_size_passes()
    {
        var issue = SizeIssue(PlanWith("Standard_D2s_v5"), Facts(Restricted("Standard_D1")));

        Assert.Null(issue);
    }

    /// <summary>
    /// The distinction that keeps an unreadable SKU list from blocking a working deployment. Null
    /// is "we could not ask"; an empty set is "we asked and nothing is restricted".
    /// </summary>
    [Fact]
    public void Unknown_restrictions_block_nothing()
    {
        Assert.Null(SizeIssue(PlanWith("Standard_D1"), Facts(null)));
        Assert.Null(SizeIssue(PlanWith("Standard_D1"), Facts(Restricted())));
    }

    [Fact]
    public void Every_blocked_size_is_named_once()
    {
        var plan = PlanWith("Standard_D1", "Standard_D2s_v5", "Standard_D11", "Standard_D1");
        var issue = SizeIssue(plan, Facts(Restricted("Standard_D1", "Standard_D11")));

        Assert.NotNull(issue);
        Assert.Contains("Standard_D1", issue.Detail);
        Assert.Contains("Standard_D11", issue.Detail);
        Assert.DoesNotContain("Standard_D2s_v5", issue.Detail);
        Assert.Contains("2 VM sizes", issue.Title);
    }

    /// <summary>
    /// Session hosts are ordinary virtual machines and there can be a great many of them, so a
    /// restricted AVD size is the most expensive version of this failure to discover late.
    /// </summary>
    [Fact]
    public void An_avd_session_host_size_is_checked_too()
    {
        var plan = PlanWith("Standard_D2s_v5");
        plan.Avd = new AvdSpec
        {
            Enabled = true,
            SessionHostCount = 3,
            VmSize = "Standard_D1"
        };

        var issue = SizeIssue(plan, Facts(Restricted("Standard_D1")));

        Assert.NotNull(issue);
        Assert.Contains("Standard_D1", issue!.Detail);
    }

    [Fact]
    public void A_disabled_server_is_not_checked()
    {
        var plan = PlanWith("Standard_D1");
        plan.Servers[0].Enabled = false;

        Assert.Null(SizeIssue(plan, Facts(Restricted("Standard_D1"))));
    }

    [Fact]
    public void Size_matching_is_case_insensitive()
    {
        var issue = SizeIssue(PlanWith("standard_d1"), Facts(Restricted("Standard_D1")));

        Assert.NotNull(issue);
    }
}
