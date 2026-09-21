using IaaSBuilder.Core;
using IaaSBuilder.Core.Dsc;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

public class RoleCatalogTests
{
    [Fact]
    public void Every_offered_role_has_a_definition() =>
        Assert.All(RoleCatalog.All, definition => Assert.True(RoleCatalog.TryGet(definition.Role, out _)));

    /// <summary>
    /// <see cref="ServerRole.CertificateAuthority"/> is a real role again, but only as a
    /// dedicated CA server.
    /// </summary>
    /// <remarks>
    /// It used to map to the <c>DC</c> token, which was wrong: <c>DCConfiguration.ps1</c> runs
    /// <c>SetupDomain FirstDS</c> - it creates a forest - so a separate "Certificate Authority"
    /// server promoted a second machine to a forest root for a domain that already existed. The
    /// package now carries its own <c>CAConfiguration.ps1</c>, which joins the existing domain and
    /// installs the CA there, so the role has its own token and cannot collide with the DC.
    /// </remarks>
    [Fact]
    public void The_certificate_authority_role_has_its_own_dsc_token()
    {
        Assert.True(RoleCatalog.TryGet(ServerRole.CertificateAuthority, out var definition));
        Assert.Equal("CA", definition!.DscToken);
        Assert.NotEqual(RoleCatalog.Get(ServerRole.DomainController).DscToken, definition.DscToken);
        Assert.True(definition.RequiresDomain);
    }

    /// <summary>
    /// Every <see cref="ServerRole"/> member has a definition today, so the graceful-degradation
    /// path is exercised with a value outside the enum - which is also what a plan file written by
    /// a newer build deserializes to. The point is that an unknown role is a validation error and
    /// not a crash.
    /// </summary>
    private const ServerRole UnknownRole = (ServerRole)9999;

    [Fact]
    public void An_unsupported_role_is_a_validation_error_rather_than_a_crash()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Servers.Add(new ServerSpec
        {
            Name = "odd01",
            Enabled = true,
            Role = UnknownRole,
            PrivateIpAddress = "10.0.1.9",
            VmSize = "Standard_D2s_v5"
        });

        using var secrets = new DeploymentSecrets("Sup3rSecret!Passw0rd");
        var result = new DeploymentPlanValidator().Validate(plan, secrets);

        Assert.Contains(result.Errors, e => e.Path.EndsWith(".role") && e.Message.Contains("no longer supported"));
    }

    [Fact]
    public void An_unsupported_role_still_renders_a_display_name() =>
        Assert.Contains("unsupported", RoleCatalog.DisplayNameOf(UnknownRole));

    /// <summary>
    /// The ARM templates build the DSC extension's configuration function as
    /// <c>concat(role, 'Configuration.ps1\Configuration')</c>. A role whose token has no
    /// matching script in the package produces a VM that provisions and then silently
    /// fails to configure - exactly what the legacy form's "Domain Join" option did,
    /// because the package only ever contained JoinDomainConfiguration.ps1.
    /// </summary>
    [Fact]
    public void Every_role_token_resolves_to_a_script_in_the_dsc_package()
    {
        var tokens = DscPackageInspector.GetAvailableRoleTokens(RepoRoot.DscPackage);
        Assert.NotEmpty(tokens);

        var missing = RoleCatalog.All
            .Where(d => !tokens.Contains(d.DscToken))
            .Select(d => $"{d.Role} -> {d.DscToken}Configuration.ps1")
            .ToList();

        Assert.True(missing.Count == 0,
            "Roles pointing at DSC scripts that do not exist: " + string.Join(", ", missing));
    }

    [Fact]
    public void Role_dependencies_are_acyclic()
    {
        foreach (var definition in RoleCatalog.All)
        {
            var visited = new HashSet<ServerRole>();
            var queue = new Queue<ServerRole>(definition.DependsOnRoles);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                Assert.NotEqual(definition.Role, current);

                if (visited.Add(current))
                {
                    foreach (var next in RoleCatalog.Get(current).DependsOnRoles)
                    {
                        queue.Enqueue(next);
                    }
                }
            }
        }
    }

    [Fact]
    public void Default_names_fit_the_windows_computer_name_limit() =>
        Assert.All(RoleCatalog.All, d =>
            Assert.True(
                RoleCatalog.CreateDefault(d.Role, "prefix").Name.Length <= Validation.AzureNaming.MaxWindowsComputerNameLength,
                $"{d.Role} default name is too long."));

    [Fact]
    public void Only_the_first_domain_controller_has_no_dependencies() =>
        Assert.All(
            RoleCatalog.All.Where(d => d.Role != ServerRole.DomainController),
            d => Assert.NotEmpty(d.DependsOnRoles));
}

public class ArmTemplateTests
{
    private static readonly string[] AllTemplates =
    [
        TemplatePaths.VirtualMachine,
        TemplatePaths.VirtualMachineSpot,
        TemplatePaths.VirtualMachineSaca,
        TemplatePaths.Networking,
        TemplatePaths.Bastion,
        TemplatePaths.Avd,
        TemplatePaths.Saca1TierNetwork,
        TemplatePaths.Saca3TierNetwork,
        TemplatePaths.Saca1TierF5,
        TemplatePaths.Saca3TierF5,
        TemplatePaths.Saca3TierIps
    ];

    [Fact]
    public void Every_referenced_template_exists_and_parses()
    {
        var resolver = new TemplateResolver(RepoRoot.Path);

        foreach (var path in AllTemplates)
        {
            var template = resolver.Load(path);
            Assert.True(template.Parameters.Count > 0, $"{path} declared no parameters.");
        }
    }

    [Fact]
    public void Admin_password_is_declared_as_a_secure_parameter()
    {
        var resolver = new TemplateResolver(RepoRoot.Path);

        foreach (var path in AllTemplates)
        {
            var template = resolver.Load(path);

            foreach (var parameter in template.Parameters.Values)
            {
                if (parameter.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    parameter.Name.Contains("Password", StringComparison.Ordinal))
                {
                    Assert.True(parameter.IsSecure,
                        $"{Path.GetFileName(path)} declares '{parameter.Name}' as '{parameter.Type}'. " +
                        "Non-secure password parameters are recorded in plain text in ARM deployment history.");
                }
            }
        }
    }

    [Fact]
    public void Bind_drops_parameters_the_template_does_not_declare()
    {
        var template = new TemplateResolver(RepoRoot.Path).Networking();

        var bound = template.Bind(new Dictionary<string, object?>
        {
            ["prefix"] = "lab",
            ["thisIsNotATemplateParameter"] = "x"
        });

        Assert.Contains("prefix", bound.Values.Keys);
        Assert.Contains("thisIsNotATemplateParameter", bound.Ignored);
        Assert.DoesNotContain("thisIsNotATemplateParameter", bound.Values.Keys);
    }

    [Fact]
    public void Bind_normalises_to_the_casing_the_template_declares()
    {
        var template = new TemplateResolver(RepoRoot.Path).Networking();

        var bound = template.Bind(new Dictionary<string, object?> { ["PREFIX"] = "lab" });

        Assert.Contains("prefix", bound.Values.Keys);
    }

    /// <summary>
    /// The real integration test: a default plan must satisfy every required parameter of
    /// every template it will actually deploy. The legacy script only discovered a missing
    /// parameter when ARM rejected the deployment.
    /// </summary>
    [Fact]
    public void A_default_plan_satisfies_every_required_template_parameter()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Avd = new AvdSpec
        {
            Enabled = true,
            HostPoolName = "lab-hp",
            MetadataLocation = "eastus"
        };

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var resolver = new TemplateResolver(RepoRoot.Path);
        var common = TemplateParameterBinder.BuildCommon(plan, secrets, "https://example.invalid/dsc/");

        foreach (var server in plan.Servers)
        {
            var parameters = TemplateParameterBinder.BuildForServer(plan, server, common);
            var bound = resolver.ForServer(plan, server).Bind(parameters);

            Assert.True(bound.Missing.Count == 0,
                $"Server '{server.Name}' is missing template parameters: {string.Join(", ", bound.Missing)}");
        }

        var avdBound = resolver.Avd().Bind(TemplateParameterBinder.BuildForAvd(plan, plan.Avd, secrets));
        Assert.True(avdBound.Missing.Count == 0,
            "AVD template is missing parameters: " + string.Join(", ", avdBound.Missing));
    }

    [Fact]
    public void Template_paths_resolve_against_the_content_root_not_the_working_directory()
    {
        var resolver = new TemplateResolver(RepoRoot.Path);
        var resolved = resolver.Resolve(TemplatePaths.Networking);

        Assert.True(File.Exists(resolved));
        Assert.StartsWith(RepoRoot.Path, resolved, StringComparison.OrdinalIgnoreCase);
    }
}
