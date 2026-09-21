using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The rules behind the disk dropdown and the preflight check added after this live failure:
/// <code>
/// Microsoft.Compute/virtualMachines 'lablabdc01' - InvalidParameter: Requested operation cannot
/// be performed because the VM size Standard_A2_v2 does not support the storage account type
/// Premium_LRS of disk 'lablabdc01-OsDisk'.
/// </code>
/// </summary>
public class DiskCompatibilityTests
{
    private static VmSizeInfo Size(string name, bool? premium) =>
        new(name, 2, 8192, 4, premium);

    [Fact]
    public void A_size_that_cannot_do_premium_is_not_offered_premium()
    {
        var offered = DiskTypes.For(Size("Standard_A2_v2", false));

        Assert.DoesNotContain(DiskTypes.PremiumSsd, offered);
        Assert.DoesNotContain(DiskTypes.PremiumSsdV2, offered);
        Assert.Contains(DiskTypes.StandardSsd, offered);
        Assert.Contains(DiskTypes.StandardHdd, offered);
    }

    [Fact]
    public void A_premium_capable_size_is_offered_everything()
    {
        Assert.Equal(DiskTypes.All, DiskTypes.For(Size("Standard_D2s_v5", true)));
    }

    /// <summary>
    /// The important one. A snapshot captured before PremiumIO was collected deserializes every
    /// size with a null capability, and an air-gapped enclave may run that snapshot for months.
    /// Treating unknown as "no" would strip Premium SSD off every size it has.
    /// </summary>
    [Fact]
    public void An_unknown_capability_never_removes_an_option()
    {
        Assert.Equal(DiskTypes.All, DiskTypes.For(Size("Standard_D2s_v5", null)));
        Assert.Equal(DiskTypes.All, DiskTypes.For(null));
        Assert.True(DiskTypes.IsSupported(Size("Standard_D2s_v5", null), DiskTypes.PremiumSsd));
        Assert.True(DiskTypes.IsSupported(null, DiskTypes.PremiumSsd));
    }

    [Fact]
    public void Unknown_is_not_the_same_as_incapable()
    {
        Assert.False(Size("x", null).KnownToRejectPremium);
        Assert.False(Size("x", true).KnownToRejectPremium);
        Assert.True(Size("x", false).KnownToRejectPremium);
    }

    [Theory]
    [InlineData(DiskTypes.PremiumSsd, true)]
    [InlineData(DiskTypes.PremiumSsdV2, true)]
    [InlineData(DiskTypes.StandardSsd, false)]
    [InlineData(DiskTypes.StandardHdd, false)]
    public void Only_the_premium_tiers_need_a_premium_capable_size(string disk, bool needs) =>
        Assert.Equal(needs, DiskTypes.RequiresPremiumCapableSize(disk));

    /// <summary>
    /// Standard SSD, not Standard HDD. A lab that silently dropped to spinning disks would be
    /// blamed on the tool long before anyone thought to look at the disk type.
    /// </summary>
    [Fact]
    public void The_fallback_disk_is_standard_ssd_not_hdd()
    {
        Assert.Equal(DiskTypes.StandardSsd, DiskTypes.BestAvailable(Size("Standard_A2_v2", false)));
        Assert.Equal(DiskTypes.PremiumSsd, DiskTypes.BestAvailable(Size("Standard_D2s_v5", true)));
    }

    [Fact]
    public void The_preflight_reports_the_exact_combination_azure_rejected()
    {
        var issues = CatalogPreflight.Check(
            PlanWith("Standard_A2_v2", DiskTypes.PremiumSsd),
            CatalogWith(Size("Standard_A2_v2", false)));

        var issue = Assert.Single(issues, i => i.Path == "servers[0].diskType");

        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Contains("Standard_A2_v2", issue.Message);
        Assert.Contains(DiskTypes.PremiumSsd, issue.Message);
    }

    /// <summary>
    /// The snapshot is routinely months old, and this is reported as an error anyway - unlike
    /// every other check here, which softens to a warning. Premium support is a fixed property of
    /// a size, so an old snapshot is still right about it.
    /// </summary>
    [Fact]
    public void A_stale_snapshot_still_blocks_an_impossible_disk()
    {
        var catalog = CatalogWith(Size("Standard_A2_v2", false));
        catalog.CapturedUtc = DateTimeOffset.UtcNow.AddYears(-2);

        var issue = Assert.Single(
            CatalogPreflight.Check(PlanWith("Standard_A2_v2", DiskTypes.PremiumSsd), catalog),
            i => i.Path == "servers[0].diskType");

        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void An_unknown_capability_does_not_block_a_deployment()
    {
        Assert.DoesNotContain(
            CatalogPreflight.Check(
                PlanWith("Standard_A2_v2", DiskTypes.PremiumSsd),
                CatalogWith(Size("Standard_A2_v2", null))),
            i => i.Path == "servers[0].diskType");
    }

    [Fact]
    public void A_disabled_server_is_not_checked()
    {
        var plan = PlanWith("Standard_A2_v2", DiskTypes.PremiumSsd);
        plan.Servers[0].Enabled = false;

        Assert.DoesNotContain(
            CatalogPreflight.Check(plan, CatalogWith(Size("Standard_A2_v2", false))),
            i => i.Path == "servers[0].diskType");
    }

    [Fact]
    public void A_workable_combination_is_reported_as_nothing()
    {
        Assert.DoesNotContain(
            CatalogPreflight.Check(
                PlanWith("Standard_D2s_v5", DiskTypes.PremiumSsd),
                CatalogWith(Size("Standard_D2s_v5", true))),
            i => i.Path == "servers[0].diskType");
    }

    [Theory]
    [InlineData("Standard_D2s_v5", "D")]
    [InlineData("Standard_D16as_v5", "D")]
    [InlineData("Standard_A2_v2", "A")]
    [InlineData("Standard_NC24ads_A100_v4", "NC")]
    [InlineData("Standard_B2ms", "B")]
    public void Sizes_group_by_series(string name, string series) =>
        Assert.Equal(series, new VmSizeInfo(name, 2, 8192, 4).Series);

    /// <summary>
    /// A size Azure offers must always be selectable, however odd its name. Series only groups the
    /// dropdown, so an unparseable name has to degrade rather than throw.
    /// </summary>
    [Fact]
    public void An_unparseable_size_name_still_yields_a_series() =>
        Assert.Equal("", new VmSizeInfo("2999", 2, 8192, 4).Series);

    private static DeploymentPlan PlanWith(string size, string disk)
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.Location = "usgovvirginia";

        plan.Servers.Clear();
        plan.Servers.Add(new ServerSpec
        {
            Name = "labdc01",
            Role = ServerRole.DomainController,
            Enabled = true,
            VmSize = size,
            DiskType = disk,
        });

        return plan;
    }

    private static ResourceCatalog CatalogWith(params VmSizeInfo[] sizes) => new()
    {
        Cloud = "UsGovernment",
        VmSizesByLocation = { ["usgovvirginia"] = [.. sizes] },
    };
}
