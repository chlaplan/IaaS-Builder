using IaaSBuilder.Core;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The DSC package is staged in a private container, and the DSC extension fetches it from inside
/// the VM with no Azure identity. If the SAS token does not reach the template, every VM provisions
/// successfully and then fails to configure - the most expensive possible place to find a bug,
/// because it is twenty minutes into a deployment and after the resource group already has VMs in
/// it. The token was silently absent for the whole life of the rewrite; these tests exist so it
/// cannot go missing again.
/// </summary>
public class ArtifactStagingTests
{
    private const string SasTokenParameter = "_artifactsLocationSasToken";

    private static DeploymentPlan PlanWithServers()
    {
        var plan = PlanFactory.CreateDefault("contoso", "contoso.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();

        // Staging in Azure Storage is what this suite is about, and it is no longer the default.
        plan.Artifacts.UsePublicPackageUrl = false;

        return plan;
    }

    [Fact]
    public void The_common_parameters_carry_the_sas_token()
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var parameters = TemplateParameterBinder.BuildCommon(
            PlanWithServers(), secrets, "https://stage.blob.core.windows.net/", "?sig=abc");

        Assert.Equal("?sig=abc", parameters[SasTokenParameter]);
    }

    [Fact]
    public void The_network_parameters_carry_the_sas_token()
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var parameters = TemplateParameterBinder.BuildForNetwork(
            PlanWithServers(), secrets, "https://stage.blob.core.windows.net/", "?sig=abc");

        Assert.Equal("?sig=abc", parameters[SasTokenParameter]);
    }

    /// <summary>
    /// The end-to-end assertion, and the one that would actually have caught the original bug: run
    /// the real orchestrator and inspect what it would have handed to ARM.
    /// </summary>
    [Fact]
    public async Task Every_vm_deployment_receives_the_staged_sas_token()
    {
        var azure = new RecordingDeploymentService();
        var orchestrator = new DeploymentOrchestrator(azure, new TemplateResolver(RepoRoot.Path));

        var plan = PlanWithServers();

        // No workstation is seeded into a new plan any more, so this one is added on purpose:
        // the point of the test is that every VM gets the token, not just the domain controller.
        PlanFactory.AddServer(plan, ServerRole.Workstation);
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();
        Assert.True(result.Succeeded);

        var vmDeployments = plan.EnabledServers.Select(s => s.Name).ToList();
        Assert.NotEmpty(vmDeployments);

        foreach (var name in vmDeployments)
        {
            var parameters = azure.Captured[name];

            Assert.True(
                parameters.TryGetValue(SasTokenParameter, out var token),
                $"Deployment '{name}' sent no {SasTokenParameter}; the DSC extension would get 403.");

            Assert.False(
                string.IsNullOrWhiteSpace(token as string),
                $"Deployment '{name}' sent an empty {SasTokenParameter}; the DSC extension would get 403.");
        }
    }

    /// <summary>
    /// Templates concatenate the token directly onto the blob path, so it has to carry its own '?'.
    /// An operator pre-staging artifacts for an air-gapped run will paste whatever the portal gave
    /// them, which may or may not include one.
    /// </summary>
    [Theory]
    [InlineData("sv=2024&sig=abc", "?sv=2024&sig=abc")]
    [InlineData("?sv=2024&sig=abc", "?sv=2024&sig=abc")]
    [InlineData("  ?sv=2024  ", "?sv=2024")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public async Task A_pre_staged_sas_token_is_normalized(string? supplied, string expected)
    {
        var plan = PlanWithServers();
        plan.Artifacts.SkipUpload = true;
        plan.Artifacts.ArtifactsLocationOverride = "https://prestaged.blob.core.usgovcloudapi.net/";
        plan.Artifacts.ArtifactsSasTokenOverride = supplied;

        // SkipUpload returns before any network call, so no real credential is needed.
        var service = new Core.Azure.AzureDeploymentService(
            new StubCredential(), AzureCloud.UsGovernment, new TemplateResolver(RepoRoot.Path));

        var location = await service.PublishArtifactsAsync(plan, CancellationToken.None);

        Assert.Equal(expected, location.SasToken);
        Assert.Equal(plan.Artifacts.ArtifactsLocationOverride, location.BaseUri);
    }

    /// <summary>
    /// The container name is not free-form: when the package is staged in Azure Storage the
    /// templates fetch the relative path <c>dsc/Configuration.zip</c>, lower case, and blob storage
    /// is case sensitive. Renaming the container - or "tidying" it to match the local <c>DSC/</c>
    /// folder - produces a 404 that only shows up inside the VM.
    /// </summary>
    /// <remarks>
    /// That path became the <c>dscPackagePath</c> parameter's default rather than a hard-coded
    /// variable, so that a public host with different capitalisation can be used. The default is
    /// what the staged path still relies on, so that is what has to agree with the container name.
    /// </remarks>
    [Fact]
    public void The_container_name_matches_the_path_hard_coded_in_the_templates()
    {
        var plan = PlanWithServers();
        Assert.Equal("dsc", plan.Artifacts.ContainerName);

        Assert.Equal(
            PublicPackageSource.StagedPackagePath,
            $"{plan.Artifacts.ContainerName}/Configuration.zip");

        foreach (var fileName in new[] { "AzureTemplate.json", "AzureTemplateSACA.json", "AzureTemplateSpot.json" })
        {
            var template = File.ReadAllText(Path.Combine(RepoRoot.Path, "Templates", fileName));

            Assert.Contains("\"dscScript\": \"[parameters('dscPackagePath')]\"", template);
            Assert.Contains(
                $"\"defaultValue\": \"{plan.Artifacts.ContainerName}/Configuration.zip\"",
                template);
        }
    }

    /// <summary>
    /// Binding only helps if the parameter is actually declared - <c>ArmTemplate.Bind()</c> drops
    /// anything the template does not know about, silently.
    /// </summary>
    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    [InlineData("Networking.json")]
    public void The_templates_declare_the_sas_token_parameter(string fileName)
    {
        var template = ArmTemplate.Load(Path.Combine(RepoRoot.Path, "Templates", fileName));
        Assert.Contains(SasTokenParameter, template.Parameters.Keys);
    }

    /// <summary>SkipUpload returns before any network call, so the credential is never exercised.</summary>
    private sealed class StubCredential : global::Azure.Core.TokenCredential
    {
        public override global::Azure.Core.AccessToken GetToken(
            global::Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The staging path under test must not contact Azure.");

        public override ValueTask<global::Azure.Core.AccessToken> GetTokenAsync(
            global::Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The staging path under test must not contact Azure.");
    }
}
