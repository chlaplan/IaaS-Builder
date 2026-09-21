using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Deployment;

/// <param name="DeploymentName">ARM deployment name within the resource group.</param>
/// <param name="ProvisioningState">Terminal provisioning state reported by ARM.</param>
/// <param name="Outputs">Template outputs, if any.</param>
public sealed record ArmDeploymentOutcome(
    string DeploymentName,
    string ProvisioningState,
    IReadOnlyDictionary<string, object?> Outputs)
{
    public bool Succeeded =>
        ProvisioningState.Equals("Succeeded", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Azure operations the orchestrator needs.
/// </summary>
/// <remarks>
/// Behind an interface so the orchestration logic - the part that used to be an 850-line
/// click handler - can be unit tested without a subscription.
/// </remarks>
/// <summary>
/// Where the DSC package was staged, and the credential needed to read it back.
/// </summary>
/// <remarks>
/// The two travel together because they are useless apart. The templates build the package URL as
/// <c>Uri(_artifactsLocation, concat('dsc/Configuration.zip', _artifactsLocationSasToken))</c>, and
/// the DSC extension fetches that URL from inside the VM, which has no Azure identity of its own.
/// The staging account is created with public blob access disabled, so without a token every VM
/// provisions successfully and then fails to configure - twenty minutes in.
/// </remarks>
/// <param name="BaseUri">Blob endpoint with trailing slash; the container is part of the template's relative path.</param>
/// <param name="SasToken">Query string including the leading '?', or empty when no credential is needed.</param>
/// <param name="PackagePath">
/// The package's path relative to <paramref name="BaseUri"/>. Carried rather than assumed because
/// a public host can be case-sensitive: <c>dsc/Configuration.zip</c> is a 404 on
/// raw.githubusercontent.com where <c>DSC/Configuration.zip</c> is a 200.
/// </param>
public sealed record ArtifactLocation(
    string BaseUri,
    string SasToken,
    string PackagePath = PublicPackageSource.StagedPackagePath)
{
    public static readonly ArtifactLocation None = new("", "");
}

public interface IAzureDeploymentService
{
    Task EnsureResourceGroupAsync(DeploymentPlan plan, CancellationToken ct);

    /// <summary>Uploads the DSC package and returns its base URI plus a read-only SAS token.</summary>
    Task<ArtifactLocation> PublishArtifactsAsync(DeploymentPlan plan, CancellationToken ct);

    /// <summary>Resolves the dedicated host resource id, or null when not in use.</summary>
    Task<string?> ResolveDedicatedHostIdAsync(DeploymentPlan plan, CancellationToken ct);

    Task<ArmDeploymentOutcome> DeployTemplateAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct);

    /// <summary>Runs a what-if / validation pass without creating resources.</summary>
    Task<ArmDeploymentOutcome> ValidateTemplateAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct);

    /// <summary>
    /// Reads the subscription state that predicts a mid-deployment failure - the blob data role,
    /// resource provider registrations - and reports what is wrong before anything is created.
    /// </summary>
    /// <remarks>
    /// Defaulted to "nothing to report" so an implementation with no subscription behind it (the
    /// CLI's offline service, the test doubles) needs no preflight of its own. Read-only, so it
    /// runs for what-if as well.
    /// </remarks>
    Task<IReadOnlyList<PreflightIssue>> RunPreflightAsync(DeploymentPlan plan, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PreflightIssue>>([]);
}
