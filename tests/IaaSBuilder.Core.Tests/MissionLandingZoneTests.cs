using System.Text.Json;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Serialization;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Guards the Mission Landing Zone integration against the vendored <c>Templates/MLZ/mlz.json</c>.
/// </summary>
/// <remarks>
/// These tests deliberately read the real template rather than a fixture. MLZ is upstream code we
/// do not control, and the failure mode of a refresh is silent: <see cref="ArmTemplate.Bind"/>
/// drops any parameter the template does not declare, so a renamed parameter would not error -
/// the deployment would simply use upstream's default and build something other than what the UI
/// showed. Asserting against the real file turns that into a build failure.
/// </remarks>
public class MissionLandingZoneTests
{
    private static string TemplatePath =>
        Path.Combine(RepoRoot.Path, "Templates", "MLZ", "mlz.json");

    private static ArmTemplate Template() => ArmTemplate.Load(TemplatePath);

    private static DeploymentPlan Plan()
    {
        var plan = PlanFactory.CreateDefault("contoso", "contoso.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.Location = "usgovvirginia";
        return plan;
    }

    private static MlzSpec Spec() => new() { Enabled = true, Identifier = "lab01" };

    private static ValidationResult Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }

    private static JsonElement Parameters(JsonDocument document) =>
        document.RootElement.GetProperty("parameters");

    private static JsonDocument Open() =>
        JsonDocument.Parse(
            File.ReadAllText(TemplatePath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    [Fact]
    public void The_template_is_vendored_and_readable()
    {
        Assert.True(File.Exists(TemplatePath),
            $"Templates/MLZ/mlz.json is missing. It is vendored, not downloaded at runtime, " +
            $"because this tool has to work in a disconnected enclave.");

        Assert.NotEmpty(Template().Parameters);
    }

    /// <summary>
    /// The whole reason <see cref="ArmDeploymentScope"/> exists. If this regresses, the deployment
    /// is submitted to a resource group and ARM rejects it with a schema error that points nowhere
    /// near the cause.
    /// </summary>
    [Fact]
    public void The_template_is_subscription_scoped()
    {
        Assert.Equal(ArmDeploymentScope.Subscription, Template().Scope);
    }

    [Fact]
    public void Every_other_template_is_still_resource_group_scoped()
    {
        var resolver = new TemplateResolver(RepoRoot.Path);

        foreach (var path in new[]
                 {
                     TemplatePaths.VirtualMachine, TemplatePaths.Networking,
                     TemplatePaths.Bastion, TemplatePaths.Avd
                 })
        {
            Assert.Equal(ArmDeploymentScope.ResourceGroup, resolver.Load(path).Scope);
        }
    }

    /// <summary>
    /// The binder is written against upstream's parameter names. A rename upstream must fail here
    /// rather than be silently dropped by Bind().
    /// </summary>
    [Fact]
    public void Every_parameter_the_binder_emits_is_declared_by_the_template()
    {
        var template = Template();
        var emitted = MlzParameterBinder.Build(Plan(), Spec());

        var undeclared = emitted.Keys
            .Where(k => !template.Parameters.ContainsKey(k))
            .OrderBy(k => k)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These parameters are sent to Mission Landing Zone but are not declared by " +
            $"Templates/MLZ/mlz.json, so Bind() would drop them silently: {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void The_binder_supplies_every_required_parameter()
    {
        var template = Template();
        var bound = template.Bind(MlzParameterBinder.Build(Plan(), Spec()));

        Assert.Empty(bound.Missing);
    }

    /// <summary>
    /// Upstream requires exactly one parameter. If that ever grows, the UI has a new mandatory
    /// field and this should fail rather than the deployment.
    /// </summary>
    [Fact]
    public void Identifier_is_the_only_required_parameter()
    {
        var required = Template().Parameters.Values
            .Where(p => p.IsRequired)
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["identifier"], required);
    }

    /// <summary>
    /// The password and suffix parameters default to newGuid()/utcNow() expressions, which are only
    /// legal as top-level parameter defaults. Supplying our own value gains nothing and an empty
    /// string fails the declared minimum length.
    /// </summary>
    [Theory]
    [InlineData("windowsVmAdminPassword")]
    [InlineData("linuxVmAdminPasswordOrKey")]
    [InlineData("deploymentNameSuffix")]
    public void The_binder_leaves_generated_defaults_alone(string parameter)
    {
        Assert.DoesNotContain(parameter, MlzParameterBinder.Build(Plan(), Spec()).Keys);
    }

    /// <summary>
    /// The validator restates upstream's allowed values so an operator is told up front. Restated
    /// values rot; this pins them to the template.
    /// </summary>
    [Theory]
    [InlineData("environmentAbbreviation", "dev", "test", "prod")]
    [InlineData("firewallSkuTier", "Premium", "Standard")]
    [InlineData("firewallIntrusionDetectionMode", "Alert", "Deny", "Off")]
    [InlineData("firewallThreatIntelMode", "Alert", "Deny", "Off")]
    [InlineData("defenderSkuTier", "Standard", "Free")]
    [InlineData("policy", "NISTRev4", "NISTRev5", "IL5", "CMMC")]
    public void Allowed_values_still_match_what_the_validator_enforces(
        string parameter,
        params string[] expected)
    {
        using var document = Open();
        var declared = Parameters(document)
            .GetProperty(parameter)
            .GetProperty("allowedValues")
            .EnumerateArray()
            .Select(v => v.GetString())
            .ToList();

        Assert.Equal(
            expected.OrderBy(v => v, StringComparer.OrdinalIgnoreCase),
            declared.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The single-subscription case relies on these defaulting to the deployment subscription.
    /// </summary>
    [Fact]
    public void Unset_tier_subscriptions_fall_back_to_the_plan_subscription()
    {
        var plan = Plan();
        var bound = MlzParameterBinder.Build(plan, Spec());

        foreach (var key in new[]
                 {
                     "hubSubscriptionId", "identitySubscriptionId",
                     "operationsSubscriptionId", "sharedServicesSubscriptionId"
                 })
        {
            Assert.Equal(plan.Azure.SubscriptionId, bound[key]);
        }
    }

    [Fact]
    public void An_explicit_tier_subscription_is_honoured()
    {
        var hub = Guid.NewGuid().ToString();
        var plan = Plan();
        var spec = Spec();
        spec.HubSubscriptionId = hub;

        var bound = MlzParameterBinder.Build(plan, spec);

        Assert.Equal(hub, bound["hubSubscriptionId"]);
        Assert.Equal(plan.Azure.SubscriptionId, bound["operationsSubscriptionId"]);
    }

    /// <summary>
    /// The reason the template is vendored at all. If a refresh introduces a nested templateLink or
    /// a deployment script that pulls from the internet, MLZ stops working in an air-gapped enclave
    /// and the first sign would be a failed deployment inside the enclave.
    /// </summary>
    [Theory]
    [InlineData("templateLink")]
    [InlineData("_artifactsLocation")]
    [InlineData("raw.githubusercontent")]
    [InlineData("https://github")]
    [InlineData("fileUris")]
    [InlineData("deploymentScripts")]
    [InlineData("containerSettings")]
    public void The_template_fetches_nothing_at_deploy_time(string construct)
    {
        var content = File.ReadAllText(TemplatePath);

        Assert.False(content.Contains(construct, StringComparison.OrdinalIgnoreCase),
            $"Templates/MLZ/mlz.json now contains '{construct}', which suggests it reaches out at " +
            "deploy time. That breaks air-gapped use. Re-verify before accepting the refresh.");
    }

    [Fact]
    public void The_upstream_licence_is_shipped_alongside_it()
    {
        var licence = Path.Combine(RepoRoot.Path, "Templates", "MLZ", "LICENSE");

        Assert.True(File.Exists(licence), "Vendoring MIT-licensed code requires shipping the notice.");
        Assert.Contains("MIT", File.ReadAllText(licence), StringComparison.OrdinalIgnoreCase);
    }

    // ---- validator ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("toolong")]
    [InlineData("lab-1")]
    [InlineData("lab_1")]
    public void An_unusable_identifier_is_an_error(string identifier)
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.Identifier = identifier;

        Assert.Contains(Validate(plan).Errors, e => e.Path == "mlz.identifier");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("lab01")]
    [InlineData("MLZ1")]
    public void A_valid_identifier_is_accepted(string identifier)
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.Identifier = identifier;

        Assert.DoesNotContain(Validate(plan).Errors, e => e.Path == "mlz.identifier");
    }

    [Fact]
    public void A_value_outside_the_templates_allowed_set_is_an_error()
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.FirewallSkuTier = "Basic";

        var error = Assert.Single(Validate(plan).Errors, e => e.Path == "mlz.firewallSkuTier");
        Assert.Contains("Premium", error.Message);
    }

    /// <summary>
    /// Silently ineffective settings are worse than rejected ones: the operator believes they have
    /// IDPS and the SCCA VDSS control is unmet.
    /// </summary>
    [Fact]
    public void Intrusion_detection_on_the_standard_firewall_warns()
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.FirewallSkuTier = "Standard";
        plan.Mlz.FirewallIntrusionDetectionMode = "Alert";

        Assert.Contains(Validate(plan).Warnings, w => w.Path == "mlz.firewallIntrusionDetectionMode");
    }

    [Fact]
    public void The_subscription_scope_caveat_is_always_surfaced()
    {
        var plan = Plan();
        plan.Mlz = Spec();

        var warning = Assert.Single(Validate(plan).Warnings, w => w.Path == "mlz");
        Assert.Contains("Owner", warning.Message);
        Assert.Contains("Encryption At Host", warning.Message);
        Assert.Contains(plan.Azure.ResourceGroup, warning.Message);
    }

    [Fact]
    public void A_valid_mlz_plan_is_deployable()
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.EmailSecurityContact = "soc@contoso.local";

        Assert.True(Validate(plan).IsValid);
    }

    [Fact]
    public void A_plan_without_mlz_says_nothing_about_it()
    {
        var result = Validate(Plan());

        Assert.DoesNotContain(result.Warnings, w => w.Path.StartsWith("mlz"));
        Assert.DoesNotContain(result.Errors, e => e.Path.StartsWith("mlz"));
    }

    [Fact]
    public void A_malformed_tier_subscription_is_an_error()
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.IdentitySubscriptionId = "not-a-guid";

        Assert.Contains(Validate(plan).Errors, e => e.Path == "mlz.identitySubscriptionId");
    }

    // ---- orchestration ----

    private static DeploymentGraph Graph(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");

        return new DeploymentOrchestrator(
                new RecordingDeploymentService(),
                new TemplateResolver(RepoRoot.Path))
            .BuildGraph(plan, secrets);
    }

    /// <summary>
    /// MLZ creates its own resource groups, so waiting on the lab's resource group step would
    /// serialise it behind work it does not need. Asserted rather than left as a comment, because
    /// adding a dependency later would quietly cost a wave.
    /// </summary>
    /// <remarks>
    /// Both steps now sit behind the preflight check, which is the point of the preflight - it
    /// runs before anything at all is created. So the invariant is that MLZ starts in the same
    /// wave as the resource group, not that it has no dependencies.
    /// </remarks>
    [Fact]
    public void The_mlz_step_does_not_wait_for_the_lab_resource_group()
    {
        var plan = Plan();
        plan.Mlz = Spec();

        var graph = Graph(plan);
        var step = Assert.Single(graph.Steps, s => s.Id == DeploymentOrchestrator.MlzStepId);

        Assert.DoesNotContain(DeploymentOrchestrator.ResourceGroupStepId, step.DependsOn);
        Assert.Equal([DeploymentOrchestrator.PreflightStepId], step.DependsOn);
        Assert.Contains(Spec().Identifier, step.DisplayName);

        var waves = graph.GetExecutionWaves();
        var mlzWave = waves.ToList().FindIndex(w => w.Contains(DeploymentOrchestrator.MlzStepId));
        var groupWave = waves.ToList().FindIndex(w => w.Contains(DeploymentOrchestrator.ResourceGroupStepId));

        Assert.Equal(groupWave, mlzWave);
    }

    [Fact]
    public void No_mlz_step_is_added_when_it_is_disabled()
    {
        var plan = Plan();
        plan.Mlz = new MlzSpec { Enabled = false, Identifier = "lab01" };

        Assert.DoesNotContain(Graph(plan).Steps, s => s.Id == DeploymentOrchestrator.MlzStepId);
    }

    /// <summary>
    /// MLZ is an addition, not a replacement: the lab network and servers must still be built.
    /// </summary>
    [Fact]
    public void Enabling_mlz_leaves_the_rest_of_the_plan_intact()
    {
        var plan = Plan();
        plan.Mlz = Spec();

        var ids = Graph(plan).Steps.Select(s => s.Id).ToList();

        Assert.Contains(DeploymentOrchestrator.ResourceGroupStepId, ids);
        Assert.Contains(DeploymentOrchestrator.NetworkStepId, ids);
    }

    /// <summary>
    /// A plan saved with MLZ settings has to come back with them, or the operator silently
    /// redeploys something different from what they configured.
    /// </summary>
    [Fact]
    public void Mlz_settings_survive_a_save_and_load()
    {
        var plan = Plan();
        plan.Mlz = Spec();
        plan.Mlz.DeploySentinel = true;
        plan.Mlz.Policy = "IL5";
        plan.Mlz.DeployPolicy = true;
        plan.Mlz.FirewallIntrusionDetectionMode = "Deny";

        var round = DeploymentPlanSerializer.Deserialize(DeploymentPlanSerializer.Serialize(plan));

        Assert.NotNull(round.Mlz);
        Assert.True(round.Mlz!.Enabled);
        Assert.Equal("lab01", round.Mlz.Identifier);
        Assert.True(round.Mlz.DeploySentinel);
        Assert.Equal("IL5", round.Mlz.Policy);
        Assert.Equal("Deny", round.Mlz.FirewallIntrusionDetectionMode);
    }
}
