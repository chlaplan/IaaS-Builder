using System.Text.RegularExpressions;
using IaaSBuilder.Core;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Fields turn red by asking <c>ValidationState</c> for issues recorded against the dotted path
/// they declare in markup - <c>Path="artifacts.storageAccountName"</c>. The rule itself stays in
/// <see cref="DeploymentPlanValidator"/>, so there is exactly one copy of "3-24 lowercase
/// alphanumerics" and the operator is shown the same text that blocks the deployment.
///
/// The join between the two halves is a string, and nothing checks it at compile time. A typo, a
/// renamed property or a rule that moves to a different path all fail the same silent way: the
/// field simply never goes red, on a page that renders perfectly. That is the same failure the
/// checklist had when it compared render-time object identity, so it gets the same treatment -
/// asserted here against the real .razor sources.
/// </summary>
public class FieldValidationPathTests
{
    private static readonly string WebComponents =
        Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Web", "Components");

    /// <summary>
    /// Both files that can record an issue. PlanState merges the validator's issues with
    /// CatalogPreflight's, so a field bound to a path only CatalogPreflight reports is correctly
    /// wired - and scanning only the validator would call it a typo.
    /// </summary>
    private static readonly string[] IssueSources =
    [
        Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Core", "Validation", "DeploymentPlanValidator.cs"),
        Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Core", "Validation", "CatalogPreflight.cs")
    ];

    /// <summary>
    /// Collapses an indexed path to its shape, so <c>servers[0].name</c> and the validator's
    /// <c>servers[{i}].name</c> compare equal.
    /// </summary>
    private static string Shape(string path) =>
        Regex.Replace(path, @"\[[^\]]*\]", "[]");

    /// <summary>Every <c>Path="..."</c> declared on a field component in the shipped pages.</summary>
    private static Dictionary<string, string> DeclaredPaths()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(WebComponents, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // Literal paths: Path="azure.location"
            foreach (Match match in Regex.Matches(text, @"Path=""([a-zA-Z][a-zA-Z0-9.\[\]]*)"""))
            {
                found[match.Groups[1].Value] = System.IO.Path.GetFileName(file);
            }

            // Computed paths: Path=@ServerPath(server, "name") - the helper builds
            // servers[i].<field>, so the field name is what needs checking.
            foreach (Match match in Regex.Matches(text, """Path=@ServerPath\(server,\s*"([a-zA-Z]+)"\)"""))
            {
                found[$"servers[].{match.Groups[1].Value}"] = System.IO.Path.GetFileName(file);
            }
        }

        return found;
    }

    /// <summary>Every path literal either source can report an issue against.</summary>
    private static HashSet<string> ValidatorPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in IssueSources)
        {
            var source = File.ReadAllText(file);

            // Two spellings. DeploymentPlanValidator uses the factory methods -
            // ValidationIssue.Error("azure.location", ...) - including the $"{path}.name"
            // interpolated form where `path` is always $"servers[{i}]". CatalogPreflight varies
            // its severity at runtime and so uses the constructor directly:
            // new ValidationIssue(severity, $"servers[{position}].vmSize", ...).
            foreach (Match match in Regex.Matches(source, IssuePattern))
            {
                var value = match.Groups[1].Value.Replace("{path}", "servers[]");
                paths.Add(Shape(value));
            }
        }

        return paths;
    }

    private const string IssuePattern =
        """(?:ValidationIssue\.(?:Error|Warning)\(|new ValidationIssue\(\s*[a-zA-Z][\w.]*\s*,)\s*\$?"([^"]+)""";

    [Fact]
    public void Every_declared_field_path_is_one_the_validator_reports()
    {
        var validator = ValidatorPaths();

        // Guards the guard. If the regex or the file path stopped matching, every assertion
        // below would pass or fail for reasons unrelated to the paths themselves. One literal
        // from each source, so a broken read of either is caught.
        Assert.NotEmpty(validator);
        Assert.Contains("artifacts.storageAccountName", validator);
        Assert.Contains("servers[].diskType", validator);

        var declared = DeclaredPaths();
        Assert.NotEmpty(declared);

        foreach (var (path, file) in declared)
        {
            Assert.True(
                validator.Contains(Shape(path)),
                $"{file} binds a field to '{path}', which DeploymentPlanValidator never reports. "
                + "That field can never turn red. Either fix the path or add the rule.");
        }
    }

    [Fact]
    public void The_storage_account_rule_reports_the_path_the_storage_page_binds_to()
    {
        // The end-to-end version of the test above for the case the user hit: a storage account
        // name that is not all lowercase must produce an error on exactly the path the field
        // declares, or the box stays black while the deployment refuses to start.
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Artifacts.UsePublicPackageUrl = false;
        plan.Artifacts.StorageAccountName = "Lab-DSC-Account";

        var issue = Assert.Single(
            Validate(plan).Errors,
            i => i.Path == "artifacts.storageAccountName");

        Assert.Contains("lowercase", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ab", false)]                                  // too short
    [InlineData("abc", true)]
    [InlineData("my-container", true)]
    [InlineData("My-Container", false)]                        // capitals
    [InlineData("my--container", false)]                       // consecutive hyphens
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("a23456789012345678901234567890123456789012345678901234567890123", true)]  // exactly 63
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234", false)] // 64
    public void Container_names_follow_the_azure_rule(string name, bool valid) =>
        Assert.Equal(valid, AzureNaming.IsValidBlobContainerName(name));

    [Fact]
    public void An_invalid_container_name_is_reported_on_its_own_path()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Artifacts.UsePublicPackageUrl = false;
        plan.Artifacts.ContainerName = "Not Valid";

        Assert.Single(Validate(plan).Errors, i => i.Path == "artifacts.containerName");
    }

    [Fact]
    public void A_pre_staged_plan_does_not_demand_a_package_path()
    {
        // SkipUpload means nothing is uploaded from here, so requiring the local package would
        // block exactly the air-gapped case the option exists for.
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Artifacts.SkipUpload = true;
        plan.Artifacts.ArtifactsLocationOverride = "https://artifacts.example/dsc";
        plan.Artifacts.DscPackagePath = "";

        Assert.DoesNotContain(Validate(plan).Errors, i => i.Path == "artifacts.dscPackagePath");
    }

    [Fact]
    public void A_missing_package_path_blocks_an_uploading_plan()
    {
        var plan = PlanFactory.CreateDefault();
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        plan.Artifacts.SkipUpload = false;
        plan.Artifacts.UsePublicPackageUrl = false;
        plan.Artifacts.DscPackagePath = "";

        Assert.Single(Validate(plan).Errors, i => i.Path == "artifacts.dscPackagePath");
    }

    private static ValidationResult Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets("Str0ng-lab-password!");
        return new DeploymentPlanValidator().Validate(plan, secrets);
    }

    private static readonly string DeployPageSource =
        Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Web", "Components", "Pages", "DeployPage.razor");

    [Fact]
    public void The_deploy_button_is_gated_on_the_plan_being_valid()
    {
        // The Deploy button used to require only a sign-in and a password. A plan with an invalid
        // field would start, create the resource group and the network, and fail at the step that
        // finally used the bad value - which is the half-built-resource-group problem the preflight
        // exists to remove, reintroduced one layer up.
        //
        // Asserted against the source because CanRun is a private member of a component that needs
        // a Blazor renderer and four scoped services to evaluate. A source assertion is weaker than
        // a behavioural one, but the alternative here is no assertion at all, and the thing most
        // likely to go wrong is someone simplifying the condition away.
        var source = File.ReadAllText(DeployPageSource);

        var canRun = Regex.Match(source, @"private bool CanRun =>(.*?);", RegexOptions.Singleline);
        Assert.True(canRun.Success, "CanRun is no longer declared the way this test recognises.");

        Assert.Contains(
            "Blocking.Count == 0",
            canRun.Groups[1].Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_section_the_validator_reports_maps_to_a_real_page()
    {
        // Each blocking issue is listed on the Deploy page with a link to the page that edits it,
        // because "artifacts.storageAccountName" only tells you where to go if you already know.
        // The mapping is by leading path segment, and an unmapped section falls through to the
        // overview - which renders fine and sends the operator to a page that cannot fix it.
        var source = File.ReadAllText(DeployPageSource);

        var pageFor = Regex.Match(source, @"PageFor\(string path\) => Section\(path\) switch\s*\{(.*?)\};", RegexOptions.Singleline);
        Assert.True(pageFor.Success, "PageFor is no longer declared the way this test recognises.");

        var mapped = new HashSet<string>(
            Regex.Matches(pageFor.Groups[1].Value, @"""([a-zA-Z]+)""\s*(?:or|=>)")
                .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

        Assert.NotEmpty(mapped);
        Assert.Contains("artifacts", mapped);

        var sections = ValidatorPaths()
            .Select(p => p.Split('.', '[')[0])
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(sections);

        foreach (var section in sections)
        {
            Assert.True(
                mapped.Contains(section),
                $"DeploymentPlanValidator reports issues under '{section}', but DeployPage.PageFor "
                + "does not map it to a page. Those issues would link to the overview, which cannot "
                + "fix them.");
        }
    }
}