using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Regional vCPU quota. A real deployment failed on this after the resource group and the network
/// had been built, and Azure reported the same 1,500-character message once per virtual machine -
/// six copies of a problem that was readable from the subscription before anything was created.
/// </summary>
public class CoreQuotaPreflightTests
{
    private static readonly Dictionary<string, int> Catalogue = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Standard_D2s_v5"] = 2,
        ["Standard_D4s_v5"] = 4,
        ["Standard_D8s_v5"] = 8
    };

    private static DeploymentPlan PlanWith(params string[] vmSizes)
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.ResourceGroup = "rg-lab";
        plan.Azure.Location = "usgovvirginia";
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

    private static PreflightFacts Facts(CoresQuota? quota) =>
        new(null, [], new Dictionary<string, string>(), null, "the subscription", null, null, quota);

    private static IReadOnlyList<PreflightIssue> Check(DeploymentPlan plan, CoresQuota? quota) =>
        DeploymentPreflight.Check(plan, Facts(quota));

    private static bool IsQuotaIssue(PreflightIssue issue) =>
        issue.Title.Contains("vCPU quota", StringComparison.OrdinalIgnoreCase);

    /// <summary>The exact shape of the failure the user hit: 98 of 100 used, 4 more wanted.</summary>
    [Fact]
    public void A_plan_that_exceeds_the_regional_limit_is_blocked_before_anything_is_created()
    {
        var plan = PlanWith("Standard_D4s_v5");
        var issues = Check(plan, new CoresQuota(100, 98, Catalogue));

        var issue = Assert.Single(issues, IsQuotaIssue);
        Assert.Equal(PreflightSeverity.Blocking, issue.Severity);
        Assert.Contains("needs 4 vCPUs", issue.Detail);
        Assert.Contains("only 2", issue.Detail);
        Assert.Contains("usgovvirginia", issue.Detail);
    }

    [Fact]
    public void A_plan_that_fits_is_not_blocked() =>
        Assert.DoesNotContain(Check(PlanWith("Standard_D4s_v5"), new CoresQuota(100, 90, Catalogue)), IsQuotaIssue);

    /// <summary>Exactly filling the quota is allowed - Azure permits it, so this must too.</summary>
    [Fact]
    public void A_plan_that_exactly_fills_the_quota_is_allowed() =>
        Assert.DoesNotContain(Check(PlanWith("Standard_D4s_v5"), new CoresQuota(100, 96, Catalogue)), IsQuotaIssue);

    /// <summary>
    /// An unreadable quota API must never be the reason a working deployment is refused - the same
    /// rule the permissions and provider checks already follow.
    /// </summary>
    [Fact]
    public void An_unreadable_quota_blocks_nothing() =>
        Assert.DoesNotContain(Check(PlanWith("Standard_D8s_v5"), null), IsQuotaIssue);

    [Fact]
    public void A_nonsensical_limit_blocks_nothing() =>
        Assert.DoesNotContain(Check(PlanWith("Standard_D8s_v5"), new CoresQuota(0, 0, Catalogue)), IsQuotaIssue);

    /// <summary>
    /// Every VM in the plan counts, not just the first - the failure the user saw was a six-server
    /// plan where no single machine was individually too large.
    /// </summary>
    [Fact]
    public void Cores_are_summed_across_every_enabled_server()
    {
        var plan = PlanWith("Standard_D4s_v5", "Standard_D4s_v5", "Standard_D2s_v5");
        var (cores, unpriced) = DeploymentPreflight.RequiredCores(plan, Catalogue);

        Assert.Equal(10, cores);
        Assert.Empty(unpriced);
    }

    [Fact]
    public void A_disabled_server_costs_nothing()
    {
        var plan = PlanWith("Standard_D4s_v5", "Standard_D4s_v5");
        plan.Servers[1].Enabled = false;

        Assert.Equal(4, DeploymentPreflight.RequiredCores(plan, Catalogue).Cores);
    }

    /// <summary>
    /// AVD session hosts are ordinary virtual machines and there can be several, so leaving them
    /// out would under-count the largest part of some plans.
    /// </summary>
    [Fact]
    public void Avd_session_hosts_are_counted_and_multiplied()
    {
        var plan = PlanWith("Standard_D2s_v5");
        plan.Avd = new AvdSpec
        {
            Enabled = true,
            VmSize = "Standard_D4s_v5",
            SessionHostCount = 3
        };

        Assert.Equal(2 + (4 * 3), DeploymentPreflight.RequiredCores(plan, Catalogue).Cores);
    }

    [Fact]
    public void Disabled_avd_session_hosts_cost_nothing()
    {
        var plan = PlanWith("Standard_D2s_v5");
        plan.Avd = new AvdSpec { Enabled = false, VmSize = "Standard_D4s_v5", SessionHostCount = 8 };

        Assert.Equal(2, DeploymentPreflight.RequiredCores(plan, Catalogue).Cores);
    }

    /// <summary>
    /// A size the region did not report cannot be priced. Inventing a number would either block a
    /// deployment that fits or hide one that does not, so it is named instead.
    /// </summary>
    [Fact]
    public void An_unknown_vm_size_is_reported_rather_than_guessed()
    {
        var plan = PlanWith("Standard_D4s_v5", "Standard_Weird_v9");
        var (cores, unpriced) = DeploymentPreflight.RequiredCores(plan, Catalogue);

        Assert.Equal(4, cores);
        Assert.Equal(["Standard_Weird_v9"], unpriced);
    }

    /// <summary>
    /// The half-priced plan must not produce a false shortfall: what could be priced still fits,
    /// so nothing is claimed.
    /// </summary>
    [Fact]
    public void An_unknown_size_does_not_invent_a_shortfall() =>
        Assert.DoesNotContain(
            Check(PlanWith("Standard_D2s_v5", "Standard_Weird_v9"), new CoresQuota(100, 50, Catalogue)),
            IsQuotaIssue);

    /// <summary>
    /// When the priced subset alone already overruns, the answer is certain even though the total
    /// is not - and the message has to say the real shortfall is larger.
    /// </summary>
    [Fact]
    public void An_unknown_size_still_blocks_when_the_priced_servers_already_overrun()
    {
        var issues = Check(
            PlanWith("Standard_D8s_v5", "Standard_Weird_v9"),
            new CoresQuota(100, 98, Catalogue));

        var issue = Assert.Single(issues, IsQuotaIssue);
        Assert.Contains("Standard_Weird_v9", issue.Detail);
        Assert.Contains("larger", issue.Detail);
    }

    /// <summary>A plan with no VMs cannot overrun a quota, whatever the numbers say.</summary>
    [Fact]
    public void A_plan_with_no_servers_blocks_nothing() =>
        Assert.DoesNotContain(Check(PlanWith(), new CoresQuota(100, 100, Catalogue)), IsQuotaIssue);

    /// <summary>
    /// The message has to be actionable. The Azure original told the operator to raise a quota
    /// request and nothing else; deleting a VM, changing region or choosing a smaller size are
    /// usually faster in a lab.
    /// </summary>
    [Fact]
    public void The_message_offers_the_options_that_do_not_need_a_support_ticket()
    {
        var issue = Assert.Single(Check(PlanWith("Standard_D8s_v5"), new CoresQuota(100, 98, Catalogue)), IsQuotaIssue);

        Assert.Contains("another region", issue.Detail);
        Assert.Contains("smaller VM sizes", issue.Detail);
        Assert.Contains("Usage + quotas", issue.Detail);
    }
}
