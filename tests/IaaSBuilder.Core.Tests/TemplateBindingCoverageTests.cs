using IaaSBuilder.Core;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Guards against parameters being silently dropped on the way to ARM.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArmTemplate.Bind"/> filters supplied values down to the parameters a template
/// actually declares. That is what makes one binder usable against templates with different
/// contracts, but it also means a mistyped or renamed key is discarded without complaint and
/// the template quietly falls back to its default value - you would get a host pool with the
/// wrong name, or the default number of session hosts, and no error anywhere.
/// </para>
/// <para>
/// <c>Missing</c> is already asserted elsewhere, but it only catches parameters that are
/// required and absent. Almost every parameter in these templates has a default, so a dropped
/// key is invisible to that check. These tests assert the other direction: everything the
/// production binders emit must correspond to a real parameter.
/// </para>
/// </remarks>
public class TemplateBindingCoverageTests
{
    private static readonly TemplateResolver Templates = new(RepoRoot.Path);

    private static DeploymentPlan PlanWithAvd()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.Location = "eastus";
        plan.Bastion.Enabled = true;
        plan.Avd = new AvdSpec
        {
            Enabled = true,
            HostPoolName = "lab-hp",
            MetadataLocation = "eastus"
        };
        return plan;
    }

    private static void AssertNothingDropped(string templateName, BoundParameters bound) =>
        AssertNothingDroppedExcept(templateName, bound, []);

    private static void AssertNothingDroppedExcept(
        string templateName,
        BoundParameters bound,
        IReadOnlyCollection<string> expected)
    {
        var unexpected = bound.Ignored
            .Where(p => !expected.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"{templateName} does not declare these parameters, so they were silently dropped: "
            + string.Join(", ", unexpected));
    }

    [Fact]
    public void Avd_binder_emits_only_parameters_the_template_declares()
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var bound = Templates.Avd().Bind(TemplateParameterBinder.BuildForAvd(plan, plan.Avd!, secrets));

        AssertNothingDropped("AzureWVD.json", bound);
    }

    [Fact]
    public void Avd_binder_supplies_every_required_parameter()
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var bound = Templates.Avd().Bind(TemplateParameterBinder.BuildForAvd(plan, plan.Avd!, secrets));

        Assert.Empty(bound.Missing);
    }

    /// <summary>
    /// The settings an operator sets in the UI must reach ARM rather than losing to a default.
    /// </summary>
    [Theory]
    [InlineData("hostpoolName")]
    [InlineData("hostpoolType")]
    [InlineData("loadBalancerType")]
    [InlineData("vmNumberOfInstances")]
    [InlineData("vmSize")]
    [InlineData("vmGalleryImageSKU")]
    [InlineData("tokenExpirationTime")]
    [InlineData("domain")]
    public void Avd_binder_actually_supplies(string parameterName)
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var bound = Templates.Avd().Bind(TemplateParameterBinder.BuildForAvd(plan, plan.Avd!, secrets));

        Assert.Contains(parameterName, bound.Values.Keys);
    }

    [Fact]
    public void Avd_session_host_count_reaches_the_template()
    {
        var plan = PlanWithAvd();
        plan.Avd!.SessionHostCount = 7;
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var bound = Templates.Avd().Bind(TemplateParameterBinder.BuildForAvd(plan, plan.Avd, secrets));

        Assert.Equal(7, bound.Values["vmNumberOfInstances"]);
    }

    /// <summary>
    /// The registration token must be computed per deployment, not at application start, or a
    /// long editing session hands ARM an already-expired expiry.
    /// </summary>
    [Fact]
    public void Avd_token_expiry_is_in_the_future()
    {
        var plan = PlanWithAvd();
        plan.Avd!.TokenLifetimeHours = 8;
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var bound = Templates.Avd().Bind(TemplateParameterBinder.BuildForAvd(plan, plan.Avd, secrets));

        var expiry = DateTimeOffset.Parse((string)bound.Values["tokenExpirationTime"]!);

        Assert.True(expiry > DateTimeOffset.UtcNow.AddHours(7));
        Assert.True(expiry < DateTimeOffset.UtcNow.AddHours(9));
    }

    [Fact]
    public void Server_binder_emits_only_parameters_the_template_declares()
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var common = TemplateParameterBinder.BuildCommon(plan, secrets, "https://example.invalid/dsc/");

        foreach (var server in plan.Servers)
        {
            var parameters = TemplateParameterBinder.BuildForServer(plan, server, common);
            var template = Templates.ForServer(plan, server);

            AssertNothingDropped($"{System.IO.Path.GetFileName(template.Path)} (server '{server.Name}')",
                template.Bind(parameters));
        }
    }

    [Fact]
    public void Spot_server_binder_emits_only_parameters_the_spot_template_declares()
    {
        var plan = PlanWithAvd();
        var server = plan.Servers[0];
        server.UseSpotInstance = true;

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var common = TemplateParameterBinder.BuildCommon(plan, secrets, "https://example.invalid/dsc/");
        var template = Templates.ForServer(plan, server);

        AssertNothingDropped(
            System.IO.Path.GetFileName(template.Path),
            template.Bind(TemplateParameterBinder.BuildForServer(plan, server, common)));
    }

    /// <summary>
    /// Networking.json shares the VM template's contract apart from the dedicated host id,
    /// which is meaningless for a network deployment.
    /// </summary>
    private static readonly string[] VmOnlyParameters = ["DHostID"];

    /// <summary>
    /// Bastion.json declares only the common parameters - it builds no VM, so the per-server
    /// values are expected to be filtered out. Anything dropped beyond this set means a common
    /// parameter has been renamed and is no longer reaching the template.
    /// </summary>
    private static readonly string[] PerServerParameters =
        ["DHostID", "servername", "ip", "vmsize", "vmdisk", "publisher", "offer", "sku", "role"];

    [Fact]
    public void Network_binder_emits_only_parameters_the_network_template_declares()
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        AssertNothingDroppedExcept(
            "Networking.json",
            Templates.Networking().Bind(NetworkParameters(plan, secrets)),
            VmOnlyParameters);
    }

    [Fact]
    public void Bastion_binder_drops_only_the_per_server_parameters()
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        AssertNothingDroppedExcept(
            "Bastion.json",
            Templates.Bastion().Bind(NetworkParameters(plan, secrets)),
            PerServerParameters);
    }

    /// <summary>
    /// The production binder, not a copy of it - duplicating the mapping here would let the two
    /// drift and defeat the point of the test.
    /// </summary>
    private static Dictionary<string, object?> NetworkParameters(
        DeploymentPlan plan,
        DeploymentSecrets secrets) =>
        TemplateParameterBinder.BuildForNetwork(plan, secrets, "https://example.invalid/dsc/");

    [Fact]
    public void Network_and_bastion_templates_have_every_required_parameter_supplied()
    {
        var plan = PlanWithAvd();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var parameters = NetworkParameters(plan, secrets);

        Assert.Empty(Templates.Networking().Bind(parameters).Missing);
        Assert.Empty(Templates.Bastion().Bind(parameters).Missing);
    }

    /// <summary>
    /// A network-only plan has to deploy.
    /// </summary>
    /// <remarks>
    /// The validator explicitly permits a plan with no enabled servers, warning only that
    /// "only networking will be deployed". Networking.json nonetheless declares most of the VM
    /// template's contract as required with no default, so binding bare common parameters left
    /// vmsize, vmdisk, role, servername and ip missing and ARM would have rejected the whole
    /// deployment. The orchestrator now substitutes a placeholder server shape.
    /// </remarks>
    [Fact]
    public void Network_only_plan_satisfies_the_network_template()
    {
        var plan = PlanWithAvd();
        plan.Avd = null;
        foreach (var server in plan.Servers)
        {
            server.Enabled = false;
        }

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var parameters = NetworkParameters(plan, secrets);

        var network = Templates.Networking().Bind(parameters);
        Assert.Empty(network.Missing);
        AssertNothingDroppedExcept("Networking.json", network, VmOnlyParameters);

        var bastion = Templates.Bastion().Bind(parameters);
        Assert.Empty(bastion.Missing);
        AssertNothingDroppedExcept("Bastion.json", bastion, PerServerParameters);
    }

    [Fact]
    public void Network_only_plan_builds_a_graph_without_any_vm_steps()
    {
        var plan = PlanWithAvd();
        plan.Avd = null;
        foreach (var server in plan.Servers)
        {
            server.Enabled = false;
        }

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var orchestrator = new DeploymentOrchestrator(new RecordingDeploymentService(), Templates);

        var graph = orchestrator.BuildGraph(plan, secrets);

        Assert.Contains(graph.Steps, s => s.Id == DeploymentOrchestrator.NetworkStepId);
        Assert.DoesNotContain(graph.Steps, s => s.Id.StartsWith("vm:", StringComparison.Ordinal));
    }
}
