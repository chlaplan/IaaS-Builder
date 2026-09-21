using System.IO.Compression;
using System.Text;
using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// An Active Directory forest gets exactly one enterprise root CA. The setting that decides where
/// it goes is read in three places that cannot see each other - the DSC package, the template
/// binder and the validator - so each one is pinned here.
/// </summary>
public class CertificateAuthorityPlacementTests
{
    private static DeploymentPlan ValidPlan()
    {
        var plan = PlanFactory.CreateDefault("lab", "lab.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Azure.ResourceGroup = "rg-lab";
        plan.Azure.Location = "usgovvirginia";
        return plan;
    }

    private static ValidationResult Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }

    /// <summary>
    /// Every previous version installed the CA on the domain controller unconditionally. A plan
    /// file written before this setting existed deserializes with the enum's default, so the
    /// default has to be the old behaviour or an existing lab would quietly come back without a CA.
    /// </summary>
    [Fact]
    public void A_new_plan_still_puts_the_certificate_authority_on_the_domain_controller() =>
        Assert.Equal(
            CertificateAuthorityPlacement.DomainController,
            PlanFactory.CreateDefault().Identity.CertificateAuthority);

    [Theory]
    [InlineData(CertificateAuthorityPlacement.DomainController, "true")]
    [InlineData(CertificateAuthorityPlacement.DedicatedServer, "false")]
    [InlineData(CertificateAuthorityPlacement.None, "false")]
    public void The_dc_is_told_to_install_a_ca_only_when_the_ca_lives_on_it(
        CertificateAuthorityPlacement placement,
        string expected)
    {
        var plan = ValidPlan();
        plan.Identity.CertificateAuthority = placement;

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var parameters = TemplateParameterBinder.BuildCommon(plan, secrets, "https://example/", "", "Configuration.zip");

        // A string, not a bool: the DSC scripts receive it as one, exactly like STIG.
        Assert.Equal(expected, Assert.IsType<string>(parameters["installCertificateAuthority"]));
    }

    /// <summary>
    /// <see cref="ArmTemplate.Bind"/> drops undeclared parameters in silence, so a template that
    /// never declares this would build a DC with a CA no matter what the page said.
    /// </summary>
    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    [InlineData("Networking.json")]
    [InlineData("Bastion.json")]
    public void The_templates_declare_the_certificate_authority_parameter(string fileName)
    {
        var template = ArmTemplate.Load(Path.Combine(RepoRoot.Path, "Templates", fileName));

        Assert.Contains("installCertificateAuthority", template.Parameters.Keys);
    }

    /// <summary>
    /// The role is only deployable because the package gained its own configuration. Without this
    /// the VM provisions, the extension asks for CAConfiguration.ps1, and nothing configures -
    /// minutes after ARM has already reported success.
    /// </summary>
    [Fact]
    public void The_package_contains_a_standalone_ca_configuration()
    {
        using var zip = ZipFile.OpenRead(RepoRoot.DscPackage);
        var entry = zip.GetEntry("CAConfiguration.ps1");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        var text = reader.ReadToEnd();

        // It joins an existing domain rather than creating a forest - the defect that got the
        // role withdrawn in the first place.
        Assert.Contains("JoinDomain", text);
        Assert.DoesNotContain("SetupDomain", text);
        Assert.Contains("ADCS-Cert-Authority", text);
    }

    /// <summary>
    /// The DC configuration has to be able to skip the CA, or choosing a dedicated server would
    /// still leave the forest with two enterprise roots.
    /// </summary>
    [Fact]
    public void The_dc_configuration_installs_its_ca_conditionally()
    {
        using var zip = ZipFile.OpenRead(RepoRoot.DscPackage);
        using var reader = new StreamReader(zip.GetEntry("DCConfiguration.ps1")!.Open(), Encoding.UTF8);
        var text = reader.ReadToEnd();

        Assert.Contains("$InstallCA", text);
        Assert.Matches(@"if\s*\(\s*\$InstallCA\s+-eq\s+'true'\s*\)", text);

        // Wrapping a resource in an if removes it from the configuration entirely when the
        // condition is false, so nothing may depend on it by name.
        Assert.DoesNotContain("[InstallCA]InstallCA", text);
    }

    /// <summary>
    /// The extension splats the template's Properties block onto the configuration function, so a
    /// script missing the parameter fails to bind and the whole run dies.
    /// </summary>
    [Fact]
    public void Every_configuration_in_the_package_accepts_the_new_parameter()
    {
        using var zip = ZipFile.OpenRead(RepoRoot.DscPackage);

        var missing = new List<string>();
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith("Configuration.ps1", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Contains('/'))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            if (!reader.ReadToEnd().Contains("$InstallCA", StringComparison.Ordinal))
            {
                missing.Add(entry.FullName);
            }
        }

        Assert.True(missing.Count == 0,
            "Configurations with no $InstallCA parameter: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_ca_server_cannot_be_added_while_the_dc_installs_the_ca()
    {
        var plan = ValidPlan();

        Assert.False(PlanFactory.CanAddServer(plan, ServerRole.CertificateAuthority, out var reason));
        Assert.Contains("dedicated server", reason!, StringComparison.OrdinalIgnoreCase);

        plan.Identity.CertificateAuthority = CertificateAuthorityPlacement.DedicatedServer;
        Assert.True(PlanFactory.CanAddServer(plan, ServerRole.CertificateAuthority, out _));
    }

    [Fact]
    public void Only_one_ca_server_can_be_added()
    {
        var plan = ValidPlan();
        plan.Identity.CertificateAuthority = CertificateAuthorityPlacement.DedicatedServer;
        PlanFactory.AddServer(plan, ServerRole.CertificateAuthority);

        Assert.False(PlanFactory.CanAddServer(plan, ServerRole.CertificateAuthority, out _));
    }

    /// <summary>
    /// The add path is only a guard on one route in. A hand-edited plan file has to be refused
    /// too, otherwise the lab builds two enterprise root CAs and nothing says so.
    /// </summary>
    [Fact]
    public void A_ca_server_with_the_ca_on_the_dc_is_rejected()
    {
        var plan = ValidPlan();
        plan.Servers.Add(RoleCatalog.CreateDefault(ServerRole.CertificateAuthority, plan.Network.Prefix));

        Assert.Contains(Validate(plan).Errors, e => e.Path == "identity.certificateAuthority");
    }

    /// <summary>
    /// The other direction builds a server that joins the domain and is never made a CA - a
    /// success that quietly produced nothing.
    /// </summary>
    [Fact]
    public void A_dedicated_placement_with_no_ca_server_is_rejected()
    {
        var plan = ValidPlan();
        plan.Identity.CertificateAuthority = CertificateAuthorityPlacement.DedicatedServer;

        Assert.Contains(Validate(plan).Errors, e => e.Path == "identity.certificateAuthority");
    }

    [Fact]
    public void A_dedicated_ca_server_with_the_matching_placement_is_valid()
    {
        var plan = ValidPlan();
        plan.Identity.CertificateAuthority = CertificateAuthorityPlacement.DedicatedServer;
        PlanFactory.AddServer(plan, ServerRole.CertificateAuthority);

        Assert.DoesNotContain(Validate(plan).Errors, e => e.Path == "identity.certificateAuthority");
    }

    /// <summary>
    /// A disabled CA server is not deployed, so it must not satisfy the dedicated placement -
    /// otherwise the plan validates and the lab comes back with no CA at all.
    /// </summary>
    [Fact]
    public void A_disabled_ca_server_does_not_satisfy_the_dedicated_placement()
    {
        var plan = ValidPlan();
        plan.Identity.CertificateAuthority = CertificateAuthorityPlacement.DedicatedServer;
        var ca = PlanFactory.AddServer(plan, ServerRole.CertificateAuthority);
        ca.Enabled = false;

        Assert.Contains(Validate(plan).Errors, e => e.Path == "identity.certificateAuthority");
    }
}
