using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The per-VM public IP, which is now optional and off by default.
/// </summary>
/// <remarks>
/// <para>
/// The built-in policy "Network interfaces should not have public IPs"
/// (83a86a26-fd1f-447c-b59d-e51f44264114) is assigned with a <c>deny</c> effect at management-group
/// scope in many enterprise and government tenants. ARM evaluates policy before it creates
/// anything, so the denial takes down the whole VM deployment - the NIC, the VM and its DSC
/// extension - not just the address the lab never needed.
/// </para>
/// <para>
/// Someone had already hit this: the SACA template carried the public IP resource commented out by
/// hand, in one template only. A hand-edit in one of three copies is exactly the kind of fix that
/// looks done and is not.
/// </para>
/// </remarks>
public class PublicIpPolicyTests
{
    private static readonly string[] VmTemplates =
    [
        "AzureTemplate.json",
        "AzureTemplateSACA.json",
        "AzureTemplateSpot.json"
    ];

    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    [InlineData("Networking.json")]
    [InlineData("Bastion.json")]
    public void Every_template_in_the_shared_parameter_set_declares_the_switch(string fileName)
    {
        var template = ArmTemplate.Load(Path.Combine(RepoRoot.Path, "Templates", fileName));

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var bound = template.Bind(TemplateParameterBinder.BuildCommon(
            PlanFactory.CreateDefault(),
            secrets,
            "https://raw.githubusercontent.com/chlaplan/IaaS-Builder/master/DSC/"));

        Assert.True(
            bound.Values.ContainsKey("assignPublicIp"),
            $"{fileName} does not declare 'assignPublicIp', so the binder's value is dropped "
                + "silently and the template uses its own default.");
    }

    /// <summary>
    /// The default has to be "no public IP" in the template as well as in the plan: a template
    /// deployed by hand, or by an older build, must not be the one that trips the policy.
    /// </summary>
    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    public void The_template_default_is_no_public_ip(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.Path, "Templates", fileName));
        var declaration = text[text.IndexOf("\"assignPublicIp\"", StringComparison.Ordinal)..];

        // The nested "metadata" object closes first, so cut at this parameter's own closing brace.
        var block = declaration[..declaration.IndexOf("\r\n    },", StringComparison.Ordinal)];

        Assert.Contains("\"type\": \"bool\"", block, StringComparison.Ordinal);
        Assert.Contains("\"defaultValue\": false", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both halves matter. A condition on the address without one on the NIC's reference to it
    /// leaves the NIC pointing at a resource that was never created, and the policy still sees a
    /// publicIpAddress.id on the interface - which is the field it actually evaluates.
    /// </summary>
    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    public void The_address_and_the_interface_are_both_conditional(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.Path, "Templates", fileName));

        Assert.Contains("\"condition\": \"[parameters('assignPublicIp')]\"", text, StringComparison.Ordinal);
        Assert.Contains("\"publicIpAddress\": \"[if(parameters('assignPublicIp')", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>dependsOn</c> must be a JSON array. ARM deserialises it into a typed array <em>before</em>
    /// it evaluates any expression, so a string - even a correct expression returning an array -
    /// is rejected outright with InvalidRequestContent and the deployment never starts.
    /// </summary>
    /// <remarks>
    /// No conditional expression is needed there: when a resource's <c>condition</c> is false, ARM
    /// removes it from its dependents' required dependencies automatically.
    /// </remarks>
    [Fact]
    public void Every_dependsOn_in_every_template_is_an_array()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, "Templates"), "*.json"))
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(path), ArmTemplate.ArmJsonOptions);

            AssertDependsOnIsAnArray(document.RootElement, Path.GetFileName(path));
        }
    }

    private static void AssertDependsOnIsAnArray(System.Text.Json.JsonElement element, string fileName)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("dependsOn"))
                    {
                        Assert.True(
                            property.Value.ValueKind == System.Text.Json.JsonValueKind.Array,
                            $"{fileName}: a resource declares dependsOn as "
                                + $"{property.Value.ValueKind}. ARM only accepts an array there.");
                    }

                    AssertDependsOnIsAnArray(property.Value, fileName);
                }

                break;

            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertDependsOnIsAnArray(item, fileName);
                }

                break;
        }
    }

    /// <summary>
    /// The SACA template used to have the whole block commented out. Leaving the comments behind
    /// would mean two mechanisms for the same thing, one of them invisible to the switch.
    /// </summary>
    [Fact]
    public void No_template_still_carries_the_hand_commented_public_ip_block()
    {
        foreach (var fileName in VmTemplates)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot.Path, "Templates", fileName));

            Assert.DoesNotContain("//  \"type\": \"Microsoft.Network/publicIpAddresses\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("//\"publicIpAddress\"", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Basic SKU public IPs were retired on 30 September 2025. A public IP resource with no
    /// <c>sku</c> defaults to Basic, so the address the switch creates would be refused outright -
    /// and Standard SKU requires Static allocation, which the template used to set to Dynamic.
    /// </summary>
    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    public void The_optional_public_ip_is_a_standard_sku(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.Path, "Templates", fileName));
        var resource = text[text.IndexOf("\"type\": \"Microsoft.Network/publicIpAddresses\"", StringComparison.Ordinal)..];
        var block = resource[..resource.IndexOf("\"type\": \"Microsoft.Network/networkInterfaces\"", StringComparison.Ordinal)];

        Assert.Contains("\"name\": \"Standard\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\"publicIpAllocationMethod\": \"Dynamic\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_plan_asks_for_no_public_ip()
    {
        var plan = PlanFactory.CreateDefault();

        Assert.False(plan.Network.AssignPublicIps);

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var parameters = TemplateParameterBinder.BuildCommon(plan, secrets, "https://example.invalid/");

        Assert.Equal(false, parameters["assignPublicIp"]);
    }

    [Fact]
    public void Asking_for_one_is_allowed_but_warned_about()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Network.AssignPublicIps = true;

        var result = Validate(plan);

        // The default plan starts blank on purpose, so it is not valid as a whole; what matters is
        // that wanting a public IP is advice rather than a rule, because some tenants permit it.
        Assert.DoesNotContain(result.Issues, i =>
            i.Severity == ValidationSeverity.Error &&
            i.Path == "network.assignPublicIps");

        Assert.Contains(result.Issues, i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Path == "network.assignPublicIps");

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var parameters = TemplateParameterBinder.BuildCommon(plan, secrets, "https://example.invalid/");

        Assert.Equal(true, parameters["assignPublicIp"]);
    }

    /// <summary>
    /// Turning the address off without Bastion on builds machines nobody can sign in to - a
    /// failure that only surfaces after a successful deployment.
    /// </summary>
    [Fact]
    public void No_bastion_and_no_public_ip_is_flagged()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Bastion.Enabled = false;
        plan.Network.AssignPublicIps = false;

        Assert.NotEmpty(plan.EnabledServers);

        Assert.Contains(Validate(plan).Issues, i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Path == "bastion.enabled");
    }

    [Fact]
    public void Bastion_on_its_own_is_enough()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Bastion.Enabled = true;
        plan.Network.AssignPublicIps = false;

        Assert.DoesNotContain(Validate(plan).Issues, i => i.Path == "bastion.enabled");
    }

    /// <summary>
    /// ARM rejects a policy-denied template up front, so there are no deployment operations to read
    /// back: the only description of what happened is a 2 KB JSON body naming the policy by GUID.
    /// </summary>
    [Fact]
    public void A_policy_denial_is_translated_into_the_fix()
    {
        const string Reason =
            "Per policy, network interfaces should not have public IPs. If you require an "
            + "exception, please reach out to the GFIM-GAs distro.";

        var raw =
            "The template deployment failed because of policy violation. Status: 400 (Bad Request) "
            + "ErrorCode: InvalidTemplateDeployment Content: {\"error\":{\"code\":\"InvalidTemplateDeployment\","
            + "\"details\":[{\"code\":\"RequestDisallowedByPolicy\",\"target\":\"lablabdc01-ni\","
            + $"\"message\":\"Resource 'lablabdc01-ni' was disallowed by policy. Reasons: '{Reason}'.\","
            + "\"additionalInfo\":[{\"info\":{\"evaluationDetails\":{\"evaluatedExpressions\":[{\"path\":"
            + "\"properties.ipConfigurations[*].properties.publicIpAddress.id\"}]}}}]}]}}";

        var explained = AzureDeploymentService.ExplainPolicyDenial(raw);

        Assert.NotNull(explained);
        Assert.Contains(Reason, explained, StringComparison.Ordinal);
        Assert.Contains("Give each VM a public IP", explained, StringComparison.Ordinal);
        Assert.Contains("Bastion", explained, StringComparison.Ordinal);
    }

    /// <summary>
    /// A denial for something else must not be given advice about public IPs, which would send the
    /// operator to the wrong page.
    /// </summary>
    [Fact]
    public void A_denial_about_something_else_keeps_the_policy_reason_and_nothing_more()
    {
        var raw =
            "{\"error\":{\"details\":[{\"code\":\"RequestDisallowedByPolicy\","
            + "\"message\":\"Resource 'labsa1' was disallowed by policy. "
            + "Reasons: 'Storage accounts must not allow public blob access.'.\"}]}}";

        var explained = AzureDeploymentService.ExplainPolicyDenial(raw);

        Assert.NotNull(explained);
        Assert.Contains("Storage accounts must not allow public blob access.", explained, StringComparison.Ordinal);
        Assert.DoesNotContain("Give each VM a public IP", explained, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrelated_failure_is_left_alone()
    {
        Assert.Null(AzureDeploymentService.ExplainPolicyDenial(
            "Microsoft.Network/virtualNetworks/subnets 'lab-vnet/AzureBastionSubnet' - NetcfgSubnetRangeOutsideVnet"));

        Assert.Null(AzureDeploymentService.ExplainPolicyDenial(null));
    }

    private static ValidationResult Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }
}
