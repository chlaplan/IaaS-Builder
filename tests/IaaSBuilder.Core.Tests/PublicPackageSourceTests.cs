using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Fetching the DSC package straight from a public HTTPS URL, instead of staging it in a storage
/// account.
/// </summary>
/// <remarks>
/// <para>
/// Staging in Azure Storage is what drags in the blob data-plane role that subscription Owner and
/// Contributor do not grant, and it is the single most common reason a deployment here fails. The
/// DSC extension never required Azure Storage - it needs a URL it can reach from inside the VM -
/// so serving the package from the project's own repository removes the dependency outright.
/// </para>
/// <para>
/// The trap this is guarding is capitalisation. raw.githubusercontent.com is case-sensitive:
/// <c>DSC/Configuration.zip</c> returns 200 and <c>dsc/Configuration.zip</c> returns 404. The
/// templates used to rebuild the path from a lowercase constant, so a correct URL would still have
/// produced a 404 - inside the VM, after every machine had been built.
/// </para>
/// </remarks>
public class PublicPackageSourceTests
{
    [Fact]
    public void The_published_package_url_splits_into_a_base_and_a_file_name()
    {
        Assert.True(PublicPackageSource.TrySplit(PublicPackageSource.DefaultUrl, out var baseUri, out var file));

        Assert.Equal("https://raw.githubusercontent.com/chlaplan/IaaS-Builder/master/DSC/", baseUri);
        Assert.Equal("Configuration.zip", file);
    }

    /// <summary>
    /// Splitting at the last slash is what keeps the host's capitalisation intact: the folder stays
    /// in the base, so nothing ever reassembles it from a lowercase constant.
    /// </summary>
    [Fact]
    public void The_folder_capitalisation_survives_the_split()
    {
        Assert.True(PublicPackageSource.TrySplit(PublicPackageSource.DefaultUrl, out var baseUri, out _));

        Assert.Contains("/DSC/", baseUri, StringComparison.Ordinal);
        Assert.DoesNotContain("/dsc/", baseUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Recombining the two the way the ARM template does has to reproduce the original URL exactly,
    /// or the VM downloads something that is not there.
    /// </summary>
    [Fact]
    public void The_base_and_file_name_recombine_into_the_original_url()
    {
        Assert.True(PublicPackageSource.TrySplit(PublicPackageSource.DefaultUrl, out var baseUri, out var file));

        // The templates evaluate Uri(_artifactsLocation, concat(dscPackagePath, sasToken)).
        var rebuilt = new Uri(new Uri(baseUri), file).ToString();

        Assert.Equal(PublicPackageSource.DefaultUrl, rebuilt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/Configuration.zip")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com")]
    public void A_url_that_does_not_name_a_file_is_refused(string? url)
    {
        Assert.False(PublicPackageSource.TrySplit(url, out _, out _));
    }

    /// <summary>
    /// A GitHub <c>/blob/</c> link renders an HTML page. It parses fine as a URL, so only the file
    /// name reveals it, and the extension would otherwise download the page and fail to unzip it.
    /// </summary>
    [Fact]
    public void A_link_to_a_web_page_rather_than_the_file_is_warned_about()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Artifacts.PublicPackageUrl =
            "https://github.com/chlaplan/IaaS-Builder/blob/master/DSC/Configuration";

        var issue = Assert.Single(
            Validate(plan).Issues,
            i => i.Path == "artifacts.publicPackageUrl");

        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void A_malformed_package_url_blocks_the_deployment()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Artifacts.PublicPackageUrl = "not a url at all";

        var issue = Assert.Single(
            Validate(plan).Errors,
            i => i.Path == "artifacts.publicPackageUrl");

        Assert.Contains("HTTPS", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default plan must be deployable with nothing filled in beyond sign-in details. If the
    /// shipped URL failed its own validation the tool would be broken out of the box.
    /// </summary>
    [Fact]
    public void The_shipped_default_plan_needs_no_storage_and_validates_cleanly()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();

        Assert.True(plan.Artifacts.UsePublicPackageUrl);
        Assert.False(plan.Artifacts.StagesInAzureStorage);
        Assert.DoesNotContain(Validate(plan).Issues, i => i.Path.StartsWith("artifacts.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole point: no storage account means no blob data role and no Microsoft.Storage
    /// registration, so the preflight must stop demanding either. This is the check that blocked
    /// six consecutive real deployments.
    /// </summary>
    [Fact]
    public void No_blob_data_role_is_demanded_when_nothing_is_uploaded()
    {
        var plan = PlanFactory.CreateDefault();

        var issues = DeploymentPreflight.Check(plan, new PreflightFacts(
            DataActions: [],
            NotDataActions: [],
            ProviderStates: new Dictionary<string, string>(),
            EncryptionAtHostRegistered: null,
            ScopeChecked: "the subscription",
            Actions: [],
            NotActions: []));

        Assert.DoesNotContain(issues, i => i.Detail.Contains("blob data", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(DeploymentPreflight.RequiredProviders(plan), p => p == "Microsoft.Storage");
    }

    [Fact]
    public void Staging_in_storage_still_demands_the_blob_role_when_it_is_chosen()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Artifacts.UsePublicPackageUrl = false;

        Assert.True(plan.Artifacts.StagesInAzureStorage);
        Assert.Contains(DeploymentPreflight.RequiredProviders(plan), p => p == "Microsoft.Storage");
    }

    /// <summary>
    /// The file name has to reach the template as a parameter. If it were dropped, ARM would fall
    /// back to the lowercase default and fetch a path that does not exist on a case-sensitive host.
    /// </summary>
    [Theory]
    [InlineData("AzureTemplate.json")]
    [InlineData("AzureTemplateSACA.json")]
    [InlineData("AzureTemplateSpot.json")]
    [InlineData("Networking.json")]
    [InlineData("Bastion.json")]
    public void The_templates_declare_the_package_path_parameter(string fileName)
    {
        var template = ArmTemplate.Load(Path.Combine(RepoRoot.Path, "Templates", fileName));

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var bound = template.Bind(TemplateParameterBinder.BuildCommon(
            PlanFactory.CreateDefault(),
            secrets,
            "https://raw.githubusercontent.com/chlaplan/IaaS-Builder/master/DSC/",
            "",
            "Configuration.zip"));

        Assert.True(
            bound.Values.ContainsKey("dscPackagePath"),
            $"{fileName} does not declare 'dscPackagePath', so the binder's value is dropped and "
                + "ARM falls back to the lowercase default.");

        Assert.Equal("Configuration.zip", bound.Values["dscPackagePath"]);
    }

    private static ValidationResult Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }
}
