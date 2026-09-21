using IaaSBuilder.Core;
using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// A size the subscription cannot deploy, reported against the field rather than only at the
/// deploy gate.
/// </summary>
/// <remarks>
/// <para>
/// The first version of this feature kept restricted sizes in the dropdown, grouped at the bottom
/// under a "Not available to this subscription" heading. An operator picked one anyway - which is
/// the whole argument against offering a choice that cannot work - and only found out when the
/// deploy gate stopped them. The list now shows what the subscription can actually deploy, and
/// this check explains a bad size that is already in a plan file.
/// </para>
/// <para>
/// Reported at the snapshot's severity, not as a hard error. Unlike Premium SSD support, a
/// restriction is not a fixed property of a size: it can be lifted by a support request or by
/// Azure adding capacity, so an old snapshot must not harden into a block.
/// </para>
/// </remarks>
public class RestrictedSizeValidationTests
{
    private const string Location = "usgovvirginia";

    private static ResourceCatalog CatalogWith(params VmSizeInfo[] sizes)
    {
        var catalog = new ResourceCatalog { CapturedUtc = DateTimeOffset.UtcNow };
        catalog.Locations.Add(new LocationInfo(Location, "US Gov Virginia", []));
        catalog.VmSizesByLocation[Location] = sizes.ToList();
        return catalog;
    }

    private static DeploymentPlan PlanWith(string vmSize, string diskType = "StandardSSD_LRS")
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.ResourceGroup = "rg-lab";
        plan.Azure.Location = Location;
        plan.Servers.Clear();

        var server = RoleCatalog.CreateDefault(ServerRole.MemberServer, plan.Network.Prefix);
        server.Name = "srv1";
        server.VmSize = vmSize;
        server.DiskType = diskType;
        plan.Servers.Add(server);

        return plan;
    }

    /// <summary>
    /// <see cref="ValidationIssue"/> is a struct, so a plain <c>FirstOrDefault</c> would hand back
    /// a zeroed instance rather than null and every "is it absent" assertion below would quietly
    /// pass whatever the product did. Cast to the nullable form first.
    /// </summary>
    private static ValidationIssue? SizeIssue(DeploymentPlan plan, ResourceCatalog catalog) =>
        CatalogPreflight.Check(plan, catalog)
            .Where(i => i.Path == "servers[0].vmSize")
            .Cast<ValidationIssue?>()
            .FirstOrDefault();

    private static VmSizeInfo Size(string name, bool? restricted) =>
        new(name, 2, 8192, 4, PremiumIo: true, Restricted: restricted);

    [Fact]
    public void A_restricted_size_already_in_the_plan_is_reported_against_its_field()
    {
        var issue = SizeIssue(
            PlanWith("Standard_D1"),
            CatalogWith(Size("Standard_D1", restricted: true), Size("Standard_D2s_v5", false)));

        Assert.NotNull(issue);
        Assert.Contains("Standard_D1", issue!.Value.Message);
        Assert.Contains(Location, issue!.Value.Message);
        Assert.Contains("SkuNotAvailable", issue!.Value.Message);
    }

    /// <summary>
    /// The operator has just been told about a vCPU quota shortfall on a different run. Without
    /// this sentence the obvious next move is to request more quota, which cannot possibly help.
    /// </summary>
    [Fact]
    public void The_message_rules_out_quota_as_the_cause()
    {
        var issue = SizeIssue(
            PlanWith("Standard_D1"),
            CatalogWith(Size("Standard_D1", restricted: true)));

        Assert.NotNull(issue);
        Assert.Contains("quota", issue!.Value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_available_size_is_not_reported()
    {
        var issue = SizeIssue(
            PlanWith("Standard_D2s_v5"),
            CatalogWith(Size("Standard_D2s_v5", restricted: false)));

        Assert.Null(issue);
    }

    /// <summary>
    /// A snapshot captured before restrictions were recorded leaves the flag null everywhere.
    /// Treating that as restricted would report every size in an air-gapped enclave as unavailable.
    /// </summary>
    [Fact]
    public void Unknown_restriction_is_not_reported()
    {
        var issue = SizeIssue(
            PlanWith("Standard_D2s_v5"),
            CatalogWith(Size("Standard_D2s_v5", restricted: null)));

        Assert.Null(issue);
    }

    [Fact]
    public void A_stale_snapshot_only_warns()
    {
        var catalog = CatalogWith(Size("Standard_D1", restricted: true));
        catalog.CapturedUtc = DateTimeOffset.UtcNow - CatalogPreflight.StaleAfter - TimeSpan.FromDays(1);

        var issue = SizeIssue(PlanWith("Standard_D1"), catalog);

        Assert.NotNull(issue);
        Assert.Equal(ValidationSeverity.Warning, issue!.Value.Severity);
    }

    [Fact]
    public void A_fresh_snapshot_is_an_error()
    {
        var issue = SizeIssue(
            PlanWith("Standard_D1"),
            CatalogWith(Size("Standard_D1", restricted: true)));

        Assert.NotNull(issue);
        Assert.Equal(ValidationSeverity.Error, issue!.Value.Severity);
    }

    [Fact]
    public void A_disabled_server_is_not_reported()
    {
        var plan = PlanWith("Standard_D1");
        plan.Servers[0].Enabled = false;

        Assert.Null(SizeIssue(plan, CatalogWith(Size("Standard_D1", restricted: true))));
    }
}
