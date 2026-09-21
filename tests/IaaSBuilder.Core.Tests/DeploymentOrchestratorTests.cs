using IaaSBuilder.Core;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

public class DeploymentOrchestratorTests
{
    private static (DeploymentOrchestrator Orchestrator, RecordingDeploymentService Azure) Create()
    {
        var azure = new RecordingDeploymentService();
        return (new DeploymentOrchestrator(azure, new TemplateResolver(RepoRoot.Path)), azure);
    }

    private static DeploymentPlan FullPlan()
    {
        var plan = PlanFactory.CreateDefault("contoso", "contoso.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();

        // No workstation is seeded into a new plan any more, so this one is added on purpose.
        PlanFactory.AddServer(plan, ServerRole.Workstation);

        PlanFactory.AddServer(plan, ServerRole.Sql);
        PlanFactory.AddServer(plan, ServerRole.SharePoint);

        plan.Avd = new AvdSpec
        {
            Enabled = true,
            HostPoolName = "contoso-hp",
            MetadataLocation = "eastus"
        };

        return plan;
    }

    /// <summary>
    /// The headline behaviour change. The legacy script waited for the DC by calling
    /// <c>Start-Sleep -Seconds 660</c> on the UI thread and then deploying AVD regardless
    /// of whether the DC had actually finished - or even succeeded.
    /// </summary>
    [Fact]
    public void Avd_depends_on_the_domain_controller_rather_than_a_timer()
    {
        var (orchestrator, _) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var graph = orchestrator.BuildGraph(plan, secrets);

        var dc = plan.Servers.Single(s => s.Role == ServerRole.DomainController);
        var avd = graph.Steps.Single(s => s.Id == DeploymentOrchestrator.AvdStepId);

        Assert.Contains(dc.StepId, avd.DependsOn);
    }

    /// <summary>
    /// The point of the whole preflight: the first two real deployments each left a resource group
    /// behind (and once a virtual network) before failing on something knowable up front.
    /// </summary>
    [Fact]
    public async Task A_blocking_prerequisite_creates_nothing_at_all()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        azure.PreflightIssues.Add(new PreflightIssue(
            PreflightSeverity.Blocking,
            "Missing 'Storage Blob Data Contributor'",
            "Grant the role and retry."));

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(0, azure.ResourceGroupsCreated);
        Assert.Empty(azure.Deployed);

        var preflight = result.Steps.Single(s => s.Id == DeploymentOrchestrator.PreflightStepId);
        Assert.Equal(StepStatus.Failed, preflight.Status);
        Assert.Contains("Nothing was created", preflight.Error);
        Assert.Contains("Storage Blob Data Contributor", preflight.Error);

        // Every other step is skipped rather than attempted.
        Assert.All(
            result.Steps.Where(s => s.Id != DeploymentOrchestrator.PreflightStepId),
            s => Assert.Equal(StepStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task Several_blocking_prerequisites_are_reported_in_one_go()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        azure.PreflightIssues.Add(new PreflightIssue(PreflightSeverity.Blocking, "First problem", "Fix it."));
        azure.PreflightIssues.Add(new PreflightIssue(PreflightSeverity.Blocking, "Second problem", "Fix it too."));

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        var preflight = result.Steps.Single(s => s.Id == DeploymentOrchestrator.PreflightStepId);
        Assert.Contains("First problem", preflight.Error);
        Assert.Contains("Second problem", preflight.Error);
    }

    [Fact]
    public async Task A_clean_preflight_lets_the_deployment_run()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, azure.ResourceGroupsCreated);
        Assert.NotEmpty(azure.Deployed);
    }

    [Fact]
    public async Task A_warning_is_reported_but_does_not_stop_the_deployment()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        azure.PreflightIssues.Add(new PreflightIssue(
            PreflightSeverity.Warning, "Something to know", "But not fatal."));

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        Assert.True(result.Succeeded);
        var preflight = result.Steps.Single(s => s.Id == DeploymentOrchestrator.PreflightStepId);
        Assert.Equal(StepStatus.Succeeded, preflight.Status);
        Assert.Null(preflight.Error);
        Assert.Contains("Something to know", preflight.Note);
    }

    /// <summary>
    /// A tenant can deny the permissions and feature read APIs to an account that is still
    /// perfectly able to deploy. A preflight that cannot see must not be the reason a working
    /// deployment is refused.
    /// </summary>
    [Fact]
    public async Task A_preflight_that_cannot_read_the_subscription_does_not_block()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        azure.PreflightThrows = new UnauthorizedAccessException("The tenant denies this read.");

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, azure.ResourceGroupsCreated);

        var preflight = result.Steps.Single(s => s.Id == DeploymentOrchestrator.PreflightStepId);
        Assert.Equal(StepStatus.Succeeded, preflight.Status);
        Assert.Contains("could not run", preflight.Note);
    }

    [Fact]
    public async Task The_preflight_runs_before_anything_else_even_under_what_if()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        azure.PreflightIssues.Add(new PreflightIssue(PreflightSeverity.Blocking, "Blocked", "Fix it."));

        var graph = orchestrator.BuildGraph(plan, secrets, new OrchestrationOptions { WhatIf = true });
        var result = await graph.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(azure.Validated);
        Assert.Equal([DeploymentOrchestrator.PreflightStepId], graph.GetExecutionWaves()[0]);
    }

    [Fact]
    public async Task Avd_is_skipped_when_the_domain_controller_fails()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var dcName = plan.Servers.Single(s => s.Role == ServerRole.DomainController).Name;
        azure.FailDeploymentsNamed.Add(dcName);

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            StepStatus.Skipped,
            result.Steps.Single(s => s.Id == DeploymentOrchestrator.AvdStepId).Status);

        // The crucial assertion: AVD was never sent to Azure.
        Assert.DoesNotContain(azure.Deployed, d => d.StartsWith("avd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharePoint_waits_for_sql_and_the_domain_controller()
    {
        var (orchestrator, _) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var graph = orchestrator.BuildGraph(plan, secrets);

        var sharePoint = plan.Servers.Single(s => s.Role == ServerRole.SharePoint);
        var sql = plan.Servers.Single(s => s.Role == ServerRole.Sql);
        var dc = plan.Servers.Single(s => s.Role == ServerRole.DomainController);

        var step = graph.Steps.Single(s => s.Id == sharePoint.StepId);

        Assert.Contains(sql.StepId, step.DependsOn);
        Assert.Contains(dc.StepId, step.DependsOn);
    }

    [Fact]
    public void Every_vm_waits_for_the_network_and_the_artifacts()
    {
        var (orchestrator, _) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var graph = orchestrator.BuildGraph(plan, secrets);

        foreach (var server in plan.EnabledServers)
        {
            var step = graph.Steps.Single(s => s.Id == server.StepId);
            Assert.Contains(DeploymentOrchestrator.NetworkStepId, step.DependsOn);
            Assert.Contains(DeploymentOrchestrator.ArtifactsStepId, step.DependsOn);
        }
    }

    [Fact]
    public void Independent_servers_land_in_the_same_wave()
    {
        var (orchestrator, _) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var graph = orchestrator.BuildGraph(plan, secrets);
        var waves = graph.GetExecutionWaves();

        var sql = plan.Servers.Single(s => s.Role == ServerRole.Sql);
        var workstation = plan.Servers.Single(s => s.Role == ServerRole.Workstation);

        var sqlWave = waves.ToList().FindIndex(w => w.Contains(sql.StepId, StringComparer.OrdinalIgnoreCase));
        var workstationWave = waves.ToList().FindIndex(w => w.Contains(workstation.StepId, StringComparer.OrdinalIgnoreCase));

        // Both only depend on the DC, so they should be deployed concurrently.
        Assert.Equal(sqlWave, workstationWave);
    }

    [Fact]
    public async Task A_full_run_deploys_every_enabled_server_exactly_once()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var result = await orchestrator.BuildGraph(plan, secrets).RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Failed.Select(f => $"{f.DisplayName}: {f.Error}")));

        foreach (var server in plan.EnabledServers)
        {
            Assert.Single(azure.Deployed, d => d.Equals(server.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Disabled_servers_are_not_deployed()
    {
        var (orchestrator, _) = Create();
        var plan = FullPlan();
        var workstation = plan.Servers.Single(s => s.Role == ServerRole.Workstation);
        workstation.Enabled = false;

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var graph = orchestrator.BuildGraph(plan, secrets);

        Assert.DoesNotContain(graph.Steps, s => s.Id == workstation.StepId);
    }

    [Fact]
    public void An_invalid_plan_is_rejected_before_anything_is_deployed()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        plan.Servers[0].PrivateIpAddress = "192.168.99.99";   // outside the subnet

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var ex = Assert.Throws<InvalidOperationException>(() => orchestrator.BuildGraph(plan, secrets));

        Assert.Contains("not valid", ex.Message);
        Assert.Empty(azure.Deployed);
    }

    [Fact]
    public async Task WhatIf_validates_without_creating_anything()
    {
        var (orchestrator, azure) = Create();
        var plan = FullPlan();
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        var graph = orchestrator.BuildGraph(plan, secrets, new OrchestrationOptions { WhatIf = true });
        var result = await graph.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(azure.Deployed);
        Assert.NotEmpty(azure.Validated);
    }

    [Fact]
    public void Saca_plans_replace_the_standard_network_step()
    {
        var (orchestrator, _) = Create();
        var plan = PlanFactory.CreateDefault("saca", "saca.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Saca = new SacaSpec
        {
            Enabled = true,
            Tier = 3,
            VNetName = "saca-vnet",
            DnsLabel = "saca",
            Subnets = { ["Management"] = new SacaSubnet { Name = "mgmt", AddressPrefix = "10.90.1.0/24" } }
        };

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var graph = orchestrator.BuildGraph(plan, secrets);

        Assert.Contains(graph.Steps, s => s.Id == DeploymentOrchestrator.SacaNetworkStepId);
        Assert.Contains(graph.Steps, s => s.Id == DeploymentOrchestrator.SacaIpsStepId);
        Assert.DoesNotContain(graph.Steps, s => s.Id == DeploymentOrchestrator.NetworkStepId);
    }

    [Fact]
    public void One_tier_saca_has_no_ips_step()
    {
        var (orchestrator, _) = Create();
        var plan = PlanFactory.CreateDefault("saca", "saca.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Saca = new SacaSpec
        {
            Enabled = true,
            Tier = 1,
            VNetName = "saca-vnet",
            DnsLabel = "saca"
        };

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var graph = orchestrator.BuildGraph(plan, secrets);

        Assert.DoesNotContain(graph.Steps, s => s.Id == DeploymentOrchestrator.SacaIpsStepId);
    }
}

/// <summary>Records what would have been deployed instead of contacting Azure.</summary>
internal sealed class RecordingDeploymentService : IAzureDeploymentService
{
    private readonly Lock _gate = new();

    public List<string> Deployed { get; } = [];
    public List<string> Validated { get; } = [];

    /// <summary>Parameters as they would have gone to ARM, keyed by deployment name.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, object?>> Captured { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailDeploymentsNamed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Issues the preflight step will report. Empty means the subscription is ready.</summary>
    public List<PreflightIssue> PreflightIssues { get; } = [];

    /// <summary>Thrown from the preflight, to stand in for a tenant that denies the read APIs.</summary>
    public Exception? PreflightThrows { get; set; }

    public int ResourceGroupsCreated { get; private set; }

    public Task<IReadOnlyList<PreflightIssue>> RunPreflightAsync(DeploymentPlan plan, CancellationToken ct) =>
        PreflightThrows is not null
            ? Task.FromException<IReadOnlyList<PreflightIssue>>(PreflightThrows)
            : Task.FromResult<IReadOnlyList<PreflightIssue>>(PreflightIssues);

    public Task EnsureResourceGroupAsync(DeploymentPlan plan, CancellationToken ct)
    {
        lock (_gate) { ResourceGroupsCreated++; }
        return Task.CompletedTask;
    }

    public Task<ArtifactLocation> PublishArtifactsAsync(DeploymentPlan plan, CancellationToken ct) =>
        Task.FromResult(new ArtifactLocation("https://example.invalid/", "?sig=fake"));

    public Task<string?> ResolveDedicatedHostIdAsync(DeploymentPlan plan, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<ArmDeploymentOutcome> DeployTemplateAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        if (FailDeploymentsNamed.Contains(deploymentName))
        {
            return Task.FromResult(new ArmDeploymentOutcome(deploymentName, "Failed", new Dictionary<string, object?>()));
        }

        lock (_gate) { Deployed.Add(deploymentName); Captured[deploymentName] = parameters; }
        return Task.FromResult(new ArmDeploymentOutcome(deploymentName, "Succeeded", new Dictionary<string, object?>()));
    }

    public Task<ArmDeploymentOutcome> ValidateTemplateAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        lock (_gate) { Validated.Add(deploymentName); Captured[deploymentName] = parameters; }
        return Task.FromResult(new ArmDeploymentOutcome(deploymentName, "Succeeded", new Dictionary<string, object?>()));
    }
}
