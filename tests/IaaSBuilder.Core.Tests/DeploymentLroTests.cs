using Azure.Core;
using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Regression cover for the SDK upgrade that silently broke every deployment.
///
/// Azure.ResourceManager.Resources 1.12.0 deprecated the ArmDeployment* model types and its
/// generated ModelReaderWriterContext stopped registering them, but the operation source that
/// completes a deployment long-running operation still asks that context to deserialise
/// ArmDeploymentData. The result was that resource group creation succeeded, the template was
/// accepted by ARM, and then roughly twenty seconds later every deployment died with
/// "No ModelReaderWriterTypeBuilder found for ArmDeploymentData" - with the resources
/// half-created in the subscription.
///
/// Nothing in a build, a unit test or a `validate` run caught it, because the fault is in
/// completing a real long-running operation. These tests close that gap.
/// </summary>
public class DeploymentLroTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("iaasb-lro").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static DeploymentPlan Plan() => new()
    {
        Name = "lab",
        Azure = new AzureTarget
        {
            SubscriptionId = "00000000-0000-0000-0000-000000000000",
            ResourceGroup = "lab-rg",
            Location = "eastus"
        }
    };

    private ArmTemplate WriteTemplate()
    {
        var path = Path.Combine(_root, "network.json");
        File.WriteAllText(path, """
            {
              "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "parameters": {},
              "resources": []
            }
            """);

        return ArmTemplate.Load(path);
    }

    private const string DeploymentJson = """
        {
          "id": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/lab-rg/providers/Microsoft.Resources/deployments/network",
          "name": "network",
          "type": "Microsoft.Resources/deployments",
          "properties": {
            "provisioningState": "Succeeded",
            "mode": "Incremental",
            "timestamp": "2026-01-01T00:00:00Z",
            "duration": "PT1S",
            "correlationId": "00000000-0000-0000-0000-00000000000c",
            "providers": [],
            "dependencies": [],
            "outputs": {
              "virtualNetworkName": { "type": "String", "value": "lab-vnet" }
            }
          }
        }
        """;

    private const string ResourceGroupJson = """
        {
          "id": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/lab-rg",
          "name": "lab-rg",
          "location": "eastus",
          "properties": { "provisioningState": "Succeeded" }
        }
        """;

    private AzureDeploymentService Service(StubArmTransport transport) =>
        new(new StubCredential(), AzureCloud.Public, new TemplateResolver(_root), transport);

    /// <summary>
    /// The exact shape of the live failure: the deployment is submitted and the long-running
    /// operation then has to turn the response into an ArmDeploymentResource. On 1.12.0 this
    /// throws InvalidOperationException instead of returning.
    /// </summary>
    [Fact]
    public async Task DeployTemplate_completes_the_long_running_operation()
    {
        var transport = new StubArmTransport((_, uri) =>
            uri.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)
                ? (200, DeploymentJson)
                : (200, ResourceGroupJson));

        var outcome = await Service(transport).DeployTemplateAsync(
            Plan(), "network", WriteTemplate(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("Succeeded", outcome.ProvisioningState);
    }

    /// <summary>The deployment outputs are what later steps bind to, so a silent empty dictionary
    /// would be almost as damaging as the throw.</summary>
    [Fact]
    public async Task DeployTemplate_returns_the_template_outputs()
    {
        var transport = new StubArmTransport((_, uri) =>
            uri.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)
                ? (200, DeploymentJson)
                : (200, ResourceGroupJson));

        var outcome = await Service(transport).DeployTemplateAsync(
            Plan(), "network", WriteTemplate(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.True(
            outcome.Outputs.ContainsKey("virtualNetworkName"),
            $"Expected a virtualNetworkName output, got: {string.Join(", ", outcome.Outputs.Keys)}");
    }

    /// <summary>
    /// `whatif` runs through ValidateAsync, a different operation source over the same model
    /// family, so it broke in 1.12.0 too - and it is the command people reach for to reassure
    /// themselves before deploying.
    /// </summary>
    [Fact]
    public async Task ValidateTemplate_completes_the_long_running_operation()
    {
        var transport = new StubArmTransport((_, uri) =>
            uri.Contains("/validate", StringComparison.OrdinalIgnoreCase)
                ? (200, """{"properties":{"provisioningState":"Succeeded","mode":"Incremental"}}""")
                : uri.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)
                    ? (200, DeploymentJson)
                    : (200, ResourceGroupJson));

        var outcome = await Service(transport).ValidateTemplateAsync(
            Plan(), "network", WriteTemplate(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("Succeeded", outcome.ProvisioningState);
    }

    /// <summary>
    /// Deployment names must be unique per scope or a re-run collides with the previous attempt,
    /// so the submitted name is suffixed rather than used verbatim.
    /// </summary>
    [Fact]
    public async Task DeployTemplate_submits_a_unique_deployment_name()
    {
        var transport = new StubArmTransport((_, uri) =>
            uri.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)
                ? (200, DeploymentJson)
                : (200, ResourceGroupJson));

        var outcome = await Service(transport).DeployTemplateAsync(
            Plan(), "network", WriteTemplate(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.NotEqual("network", outcome.DeploymentName);
        Assert.StartsWith("network-", outcome.DeploymentName, StringComparison.Ordinal);
    }
}
