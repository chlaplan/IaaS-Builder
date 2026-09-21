using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Deployment;

public sealed record OrchestrationOptions
{
    /// <summary>Validate templates against ARM without creating anything.</summary>
    public bool WhatIf { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = 8;
}

/// <summary>
/// Turns a <see cref="DeploymentPlan"/> into a dependency graph of ARM deployments.
/// </summary>
/// <remarks>
/// This is the direct replacement for <c>$WPFBuild1.Add_Click{}</c>: ~850 lines containing
/// 25 near-identical deployment blocks, each gated on its own checkbox and each reading the
/// same dozen controls again. Here the servers are a collection, the role table supplies the
/// per-role differences, and the dependency graph supplies the ordering.
/// </remarks>
public sealed class DeploymentOrchestrator
{
    private readonly IAzureDeploymentService _azure;
    private readonly TemplateResolver _templates;

    public DeploymentOrchestrator(IAzureDeploymentService azure, TemplateResolver templates)
    {
        _azure = azure;
        _templates = templates;
    }

    public const string PreflightStepId = "preflight";
    public const string ResourceGroupStepId = "resource-group";
    public const string ArtifactsStepId = "artifacts";
    public const string NetworkStepId = "network";
    public const string BastionStepId = "bastion";
    public const string AvdStepId = "avd";
    public const string SacaNetworkStepId = "saca-network";
    public const string SacaF5StepId = "saca-f5";
    public const string SacaIpsStepId = "saca-ips";
    public const string MlzStepId = "mlz";

    /// <summary>
    /// Builds the graph without running it, so callers can preview the ordering.
    /// </summary>
    public DeploymentGraph BuildGraph(
        DeploymentPlan plan,
        DeploymentSecrets secrets,
        OrchestrationOptions? options = null)
    {
        options ??= new OrchestrationOptions();

        var validation = new DeploymentPlanValidator(
            Dsc.DscPackageInspector.GetAvailableRoleTokens(
                _templates.Resolve(plan.Artifacts.DscPackagePath)) is { Count: > 0 } tokens
                ? tokens
                : null)
            .Validate(plan, secrets);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Plan is not valid:" + Environment.NewLine +
                string.Join(Environment.NewLine, validation.Errors.Select(e => $"  - {e}")));
        }

        // Shared mutable state resolved by the early steps and consumed by later ones.
        var context = new DeploymentContext();

        // Runs before anything is created. The first two real deployments both failed part way
        // through on conditions that were readable up front, leaving a resource group and a
        // virtual network behind to be cleaned up by hand. Declared ahead of the list so the
        // closure can annotate its own step with any non-blocking findings.
        DeploymentStep preflight = null!;
        preflight = new DeploymentStep(PreflightStepId, "Check subscription prerequisites", [],
            ct => RunPreflightAsync(plan, preflight, ct));

        var steps = new List<DeploymentStep>
        {
            preflight,

            new(ResourceGroupStepId, $"Resource group '{plan.Azure.ResourceGroup}'", [PreflightStepId],
                ct => _azure.EnsureResourceGroupAsync(plan, ct)),

            new(ArtifactsStepId, "Publish DSC artifacts", [ResourceGroupStepId],
                async ct =>
                {
                    context.Artifacts = await _azure.PublishArtifactsAsync(plan, ct);
                })
        };

        List<string> networkDependencies = [ResourceGroupStepId];

        if (plan.Mlz is { Enabled: true } mlz)
        {
            // No dependency on the resource group step: MLZ is subscription-scoped and builds its
            // own resource groups, so it can run in the first wave alongside it.
            steps.Add(new DeploymentStep(MlzStepId, $"Mission Landing Zone '{mlz.Identifier}'", [PreflightStepId],
                ct => RunAsync(plan, MlzStepId, _templates.Mlz(),
                    MlzParameterBinder.Build(plan, mlz), options, ct)));
        }

        if (plan.Saca is { Enabled: true } saca)
        {
            steps.Add(new DeploymentStep(SacaNetworkStepId, $"SACA {saca.Tier}-tier network", [ResourceGroupStepId],
                ct => RunAsync(plan, SacaNetworkStepId, _templates.SacaNetwork(saca.Tier),
                    SacaParameterBinder.BuildNetwork(plan, saca), options, ct)));

            steps.Add(new DeploymentStep(SacaF5StepId, "SACA F5 appliances", [SacaNetworkStepId],
                ct => RunAsync(plan, SacaF5StepId, _templates.SacaF5(saca.Tier),
                    SacaParameterBinder.BuildAppliances(plan, saca, secrets), options, ct)));

            if (saca.Tier == 3)
            {
                steps.Add(new DeploymentStep(SacaIpsStepId, "SACA IPS pair", [SacaNetworkStepId],
                    ct => RunAsync(plan, SacaIpsStepId, _templates.SacaIps(),
                        SacaParameterBinder.BuildAppliances(plan, saca, secrets), options, ct)));
            }

            networkDependencies = [SacaNetworkStepId];
        }
        else
        {
            steps.Add(new DeploymentStep(NetworkStepId, "Virtual network", [ResourceGroupStepId],
                ct => RunAsync(plan, NetworkStepId, _templates.Networking(),
                    BuildNetworkParameters(plan, secrets, context), options, ct)));

            networkDependencies = [NetworkStepId];
        }

        if (plan.Bastion.Enabled)
        {
            steps.Add(new DeploymentStep(BastionStepId, "Azure Bastion", networkDependencies,
                ct => RunAsync(plan, BastionStepId, _templates.Bastion(),
                    BuildNetworkParameters(plan, secrets, context), options, ct)));
        }

        AddServerSteps(plan, secrets, options, context, networkDependencies, steps);
        AddAvdStep(plan, secrets, options, steps);

        return new DeploymentGraph(steps) { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
    }

    private void AddServerSteps(
        DeploymentPlan plan,
        DeploymentSecrets secrets,
        OrchestrationOptions options,
        DeploymentContext context,
        IReadOnlyList<string> networkDependencies,
        List<DeploymentStep> steps)
    {
        // Role -> step id, so a role dependency can be turned into a step dependency.
        var stepIdByRole = plan.EnabledServers
            .GroupBy(s => s.Role)
            .ToDictionary(g => g.Key, g => g.First().StepId);

        foreach (var server in plan.EnabledServers)
        {
            var definition = RoleCatalog.Get(server.Role);

            var dependsOn = new List<string>(networkDependencies) { ArtifactsStepId };
            foreach (var requiredRole in definition.DependsOnRoles)
            {
                if (stepIdByRole.TryGetValue(requiredRole, out var requiredStepId) &&
                    !string.Equals(requiredStepId, server.StepId, StringComparison.OrdinalIgnoreCase))
                {
                    dependsOn.Add(requiredStepId);
                }
            }

            var captured = server;
            steps.Add(new DeploymentStep(
                captured.StepId,
                $"{definition.DisplayName} '{captured.Name}'",
                dependsOn.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                async ct =>
                {
                    if (plan.DedicatedHost is { Enabled: true, HostId: null or "" } host)
                    {
                        host.HostId = await _azure.ResolveDedicatedHostIdAsync(plan, ct);
                    }

                    var common = TemplateParameterBinder.BuildCommon(
                        plan, secrets, context.Artifacts.BaseUri, context.Artifacts.SasToken, context.Artifacts.PackagePath);
                    var parameters = TemplateParameterBinder.BuildForServer(plan, captured, common);

                    await RunAsync(plan, captured.Name, _templates.ForServer(plan, captured), parameters, options, ct);
                }));
        }
    }

    private void AddAvdStep(
        DeploymentPlan plan,
        DeploymentSecrets secrets,
        OrchestrationOptions options,
        List<DeploymentStep> steps)
    {
        if (plan.Avd is not { Enabled: true } avd) return;

        // The real reason the legacy script slept for 11 minutes: session hosts domain join
        // during provisioning, so the DC has to be finished first. Now it is an edge, not a timer.
        var dependsOn = plan.EnabledServers
            .Where(s => s.Role == ServerRole.DomainController)
            .Select(s => s.StepId)
            .ToList();

        steps.Add(new DeploymentStep(AvdStepId, $"Azure Virtual Desktop '{avd.HostPoolName}'", dependsOn,
            ct => RunAsync(plan, AvdStepId, _templates.Avd(),
                TemplateParameterBinder.BuildForAvd(plan, avd, secrets), options, ct)));
    }

    private static Dictionary<string, object?> BuildNetworkParameters(
        DeploymentPlan plan,
        DeploymentSecrets secrets,
        DeploymentContext context) =>
        TemplateParameterBinder.BuildForNetwork(
            plan, secrets, context.Artifacts.BaseUri, context.Artifacts.SasToken, context.Artifacts.PackagePath);

    /// <summary>
    /// Fails the step when a prerequisite would stop the deployment, which skips every later step
    /// and so creates nothing. Warnings are attached to the step and the run continues.
    /// </summary>
    /// <remarks>
    /// A preflight that cannot read the subscription must not block a deployment that would have
    /// worked: a tenant can deny the permissions or feature APIs to an account that is still
    /// perfectly able to deploy. Any failure to gather the facts is reported as a note.
    /// </remarks>
    private async Task RunPreflightAsync(DeploymentPlan plan, DeploymentStep step, CancellationToken ct)
    {
        IReadOnlyList<PreflightIssue> issues;

        try
        {
            issues = await _azure.RunPreflightAsync(plan, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step.Note = $"Prerequisite checks could not run ({ex.Message}). Continuing.";
            return;
        }

        var blocking = issues.Where(i => i.Severity == PreflightSeverity.Blocking).ToList();
        var warnings = issues.Where(i => i.Severity == PreflightSeverity.Warning).ToList();

        if (blocking.Count > 0)
        {
            throw new InvalidOperationException(
                "Nothing was created. Fix these first:" + Environment.NewLine +
                string.Join(Environment.NewLine, blocking.Select(i => $"  - {i.Title}: {i.Detail}")));
        }

        if (warnings.Count > 0)
        {
            step.Note = string.Join(" | ", warnings.Select(i => $"{i.Title}: {i.Detail}"));
        }
    }

    private async Task RunAsync(
        DeploymentPlan plan,
        string deploymentName,
        ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters,
        OrchestrationOptions options,
        CancellationToken ct)
    {
        var bound = template.Bind(parameters);

        if (bound.Missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Template '{Path.GetFileName(template.Path)}' requires parameters that were not supplied: " +
                string.Join(", ", bound.Missing));
        }

        var outcome = options.WhatIf
            ? await _azure.ValidateTemplateAsync(plan, deploymentName, template, bound.Values, ct)
            : await _azure.DeployTemplateAsync(plan, deploymentName, template, bound.Values, ct);

        if (!outcome.Succeeded)
        {
            throw new InvalidOperationException(
                $"Deployment '{deploymentName}' finished with state '{outcome.ProvisioningState}'.");
        }
    }

    private sealed class DeploymentContext
    {
        public ArtifactLocation Artifacts { get; set; } = ArtifactLocation.None;
    }
}
