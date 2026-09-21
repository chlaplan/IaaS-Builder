using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The staging account is created fresh, under a new random name, in the lab's resource group on
/// every run. So an operator who finally obtains 'Storage Blob Data Contributor' on it gets no
/// benefit at all on the next deployment - the account that grant applies to no longer exists.
/// </summary>
/// <remarks>
/// That is the loop four real deployments were stuck in. Nominating a long-lived account the
/// operator already has blob access on breaks it without needing any role assignment to be
/// created at deployment time, which Contributor cannot do anyway.
/// </remarks>
public class NominatedStagingAccountTests
{
    /// <summary>A placeholder, not anyone's real subscription. This repository is public.</summary>
    private const string SampleSubscriptionId = "00000000-1111-2222-3333-444444444444";

    private static DeploymentPlan PlanWith(string stagingGroup)
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = SampleSubscriptionId;
        plan.Azure.ResourceGroup = "rg-lab55";

        // Nominating a staging account is only meaningful when one is actually used, which is no
        // longer the default now that the package can be fetched from a public URL.
        plan.Artifacts.UsePublicPackageUrl = false;
        plan.Artifacts.StorageResourceGroup = stagingGroup;
        return plan;
    }

    [Fact]
    public void An_unset_staging_group_still_means_the_labs_own_resource_group()
    {
        Assert.Equal("rg-lab55", AzureDeploymentService.StagingResourceGroup(PlanWith("")));
    }

    [Fact]
    public void A_nominated_staging_group_is_used_instead()
    {
        Assert.Equal("shared-artifacts", AzureDeploymentService.StagingResourceGroup(PlanWith("shared-artifacts")));
    }

    /// <summary>
    /// Whitespace is not a nomination. Treating " " as a resource group name would send both the
    /// account lookup and the role-grant advice to a group that cannot exist.
    /// </summary>
    [Fact]
    public void Whitespace_is_treated_as_unset()
    {
        Assert.Equal("rg-lab55", AzureDeploymentService.StagingResourceGroup(PlanWith("   ")));
    }

    /// <summary>
    /// The whole point of nominating a group is that the grant is made once, on that account. Advice
    /// naming the lab's resource group would send the operator to grant access somewhere the
    /// storage account is not, and the next run would fail identically.
    /// </summary>
    [Fact]
    public void The_role_grant_advice_is_scoped_to_the_account_that_actually_failed()
    {
        var plan = PlanWith("shared-artifacts");

        var message = AzureDeploymentService.ExplainUploadFailure(
            AzureDeploymentService.SharedKeyStatus.CannotListKeys,
            plan.Artifacts.StorageAccountName,
            AzureDeploymentService.StagingResourceGroup(plan),
            plan.Azure.SubscriptionId);

        Assert.Contains("resourceGroups/shared-artifacts", message, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceGroups/rg-lab55", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_staging_group_is_valid_because_it_is_optional()
    {
        Assert.DoesNotContain(
            Validate(PlanWith("")),
            i => i.Path == "artifacts.storageResourceGroup");
    }

    [Fact]
    public void A_nonsense_staging_group_name_is_rejected_before_deploying()
    {
        var issue = Assert.Single(
            Validate(PlanWith("not a valid group!!")),
            i => i.Path == "artifacts.storageResourceGroup");

        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    private static IReadOnlyList<ValidationIssue> Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        return new DeploymentPlanValidator().Validate(plan, secrets).Issues;
    }
}
