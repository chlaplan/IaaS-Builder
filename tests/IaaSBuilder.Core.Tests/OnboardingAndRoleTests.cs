using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Covers the guided checklist, the password generator, the trust-tier grouping, and the
/// Configuration Manager local-SQL requirement.
/// </summary>
public class OnboardingAndRoleTests
{
    // ---- Password generator -------------------------------------------------------------

    [Fact]
    public void Generated_passwords_satisfy_the_policy()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordGenerator.Generate("labadmin");
            Assert.Empty(PasswordPolicy.Check(password, "labadmin"));
        }
    }

    /// <summary>
    /// A single-character username is the pathological case: roughly a 45% chance that any given
    /// draw contains it, so the generator has to retry rather than return something Azure rejects.
    /// </summary>
    [Fact]
    public void Generation_survives_a_username_that_is_likely_to_appear_by_chance()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordGenerator.Generate("a");
            Assert.DoesNotContain("a", password, StringComparison.OrdinalIgnoreCase);
            Assert.True(PasswordPolicy.IsValid(password, "a"));
        }
    }

    [Fact]
    public void Generated_passwords_are_not_all_the_same()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 100; i++)
        {
            seen.Add(PasswordGenerator.Generate());
        }

        Assert.Equal(100, seen.Count);
    }

    /// <summary>
    /// These end up inside PowerShell DSC configurations and ARM parameters, where quotes,
    /// backticks, backslashes and shell metacharacters cause failures a long way from here.
    /// </summary>
    [Theory]
    [InlineData('"')]
    [InlineData('\'')]
    [InlineData('`')]
    [InlineData('\\')]
    [InlineData('$')]
    [InlineData('&')]
    [InlineData('|')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(';')]
    public void Generated_passwords_avoid_characters_that_break_the_scripting_path(char forbidden)
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.DoesNotContain(forbidden, PasswordGenerator.Generate());
        }
    }

    [Fact]
    public void Generated_length_is_clamped_to_the_policy_bounds()
    {
        Assert.Equal(PasswordPolicy.MinLength, PasswordGenerator.Generate(length: 1).Length);
        Assert.Equal(PasswordPolicy.MaxLength, PasswordGenerator.Generate(length: 9999).Length);
        Assert.Equal(32, PasswordGenerator.Generate(length: 32).Length);
    }

    // ---- Trust tiers --------------------------------------------------------------------

    [Fact]
    public void Identity_roles_are_tier_0_and_workstations_are_tier_2()
    {
        Assert.Equal(TrustTier.Tier0, RoleCatalog.Get(ServerRole.DomainController).Tier);
        Assert.Equal(TrustTier.Tier0, RoleCatalog.Get(ServerRole.AdditionalDomainController).Tier);
        Assert.Equal(TrustTier.Tier0, RoleCatalog.Get(ServerRole.Adfs).Tier);
        Assert.Equal(TrustTier.Tier2, RoleCatalog.Get(ServerRole.Workstation).Tier);
    }

    [Fact]
    public void Application_roles_are_tier_1()
    {
        foreach (var role in new[]
                 {
                     ServerRole.Exchange, ServerRole.SharePoint, ServerRole.Sql,
                     ServerRole.SccmPrimarySite, ServerRole.SccmDistributionPoint,
                     ServerRole.MemberServer
                 })
        {
            Assert.Equal(TrustTier.Tier1, RoleCatalog.Get(role).Tier);
        }
    }

    /// <summary>
    /// The grouping only makes sense if nothing falls outside it, and the UI iterates
    /// <see cref="TrustTiers.InOrder"/> rather than the enum, so a tier missing from that list
    /// would silently hide every role in it.
    /// </summary>
    [Fact]
    public void Every_role_lands_in_a_tier_the_ui_actually_renders()
    {
        foreach (var definition in RoleCatalog.All)
        {
            Assert.Contains(definition.Tier, TrustTiers.InOrder);
        }

        Assert.Equal(
            Enum.GetValues<TrustTier>().Length,
            TrustTiers.InOrder.Distinct().Count());
    }

    [Fact]
    public void Tiers_are_presented_most_privileged_first()
    {
        Assert.Equal([TrustTier.Tier0, TrustTier.Tier1, TrustTier.Tier2], TrustTiers.InOrder);
    }

    // ---- Configuration Manager needs SQL on its own VM ----------------------------------

    /// <summary>
    /// <c>InstallAndUpdateSCCM.ps1</c> reads the local registry for
    /// <c>InstalledInstances[0]</c> and points the site database at its own computer name, so the
    /// primary site VM must carry SQL. It ships with a plain Windows Server image and fails
    /// mid-DSC otherwise.
    /// </summary>
    [Fact]
    public void Configuration_manager_primary_site_defaults_to_a_sql_image()
    {
        var image = RoleCatalog.Get(ServerRole.SccmPrimarySite).DefaultImage;
        Assert.Equal(ImageDefaults.SqlPublisher, image.Publisher);
    }

    /// <summary>
    /// Adding Sql to DependsOnRoles would look like the fix and is not: ConfigMgr never contacts
    /// the separate server, so the operator would pay for a VM that does nothing.
    /// </summary>
    [Fact]
    public void Configuration_manager_does_not_depend_on_a_separate_sql_server()
    {
        Assert.DoesNotContain(
            ServerRole.Sql,
            RoleCatalog.Get(ServerRole.SccmPrimarySite).DependsOnRoles);
    }

    [Fact]
    public void A_primary_site_on_a_plain_windows_image_is_warned_about()
    {
        var plan = PlanWithPrimarySite(ImageDefaults.WindowsServer);
        var issues = Validate(plan);

        var warning = Assert.Single(issues, i =>
            i.Path.EndsWith(".image", StringComparison.Ordinal));

        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Contains("same VM", warning.Message);
    }

    [Fact]
    public void A_primary_site_on_a_sql_image_is_not_warned_about()
    {
        var plan = PlanWithPrimarySite(ImageDefaults.SqlServer);
        Assert.DoesNotContain(Validate(plan), i => i.Path.EndsWith(".image", StringComparison.Ordinal));
    }

    /// <summary>
    /// An enclave may carry a custom image whose offer names SQL without using Microsoft's
    /// publisher. Blocking that would be the "unknown means absent" mistake.
    /// </summary>
    [Fact]
    public void A_custom_image_whose_offer_names_sql_is_accepted()
    {
        var plan = PlanWithPrimarySite(new ImageReferenceSpec
        {
            Publisher = "ContosoImages",
            Offer = "windows-sql-2022",
            Sku = "standard"
        });

        Assert.DoesNotContain(Validate(plan), i => i.Path.EndsWith(".image", StringComparison.Ordinal));
    }

    // ---- Getting-started checklist ------------------------------------------------------

    [Fact]
    public void An_empty_plan_starts_at_the_first_step()
    {
        var steps = SetupChecklist.For(new DeploymentPlan(), signedIn: false, adminPassword: null);

        Assert.All(steps, s => Assert.False(s.Done));
        Assert.Equal("Choose the cloud", SetupChecklist.NextStep(steps)!.Title);
    }

    [Fact]
    public void Signing_in_completes_the_cloud_and_sign_in_steps()
    {
        var steps = SetupChecklist.For(new DeploymentPlan(), signedIn: true, adminPassword: null);

        Assert.True(steps[0].Done);
        Assert.True(steps[1].Done);
        Assert.Equal("Pick a subscription", SetupChecklist.NextStep(steps)!.Title);
    }

    [Fact]
    public void Steps_track_the_plan_rather_than_a_fixed_script()
    {
        var plan = new DeploymentPlan();
        plan.Azure.SubscriptionId = "0000";
        plan.Azure.Location = "eastus";
        plan.Azure.ResourceGroup = "lab-rg";
        plan.Identity.DomainName = "contoso.local";
        plan.Identity.AdminUsername = "labadmin";

        var steps = SetupChecklist.For(plan, signedIn: true, PasswordGenerator.Generate("labadmin"));

        Assert.Equal("Add the servers you want", SetupChecklist.NextStep(steps)!.Title);
    }

    [Fact]
    public void A_password_that_fails_policy_leaves_its_step_unfinished()
    {
        var plan = new DeploymentPlan();
        plan.Identity.AdminUsername = "labadmin";

        var steps = SetupChecklist.For(plan, signedIn: true, "short");
        var password = steps.Single(s => s.Title == "Set an administrator password");

        Assert.False(password.Done);
    }

    [Fact]
    public void Every_step_points_at_a_page_that_exists()
    {
        // Reads the real @page routes rather than a list written here. The hardcoded list this
        // replaced would have passed happily after Identity moved to its own page, because the
        // list and the checklist were both edited by hand and neither knew about the routes.
        var routes = Directory
            .EnumerateFiles(
                System.IO.Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Web", "Components", "Pages"),
                "*.razor",
                SearchOption.AllDirectories)
            .SelectMany(f => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(f), @"@page\s+""/([^""]*)""")
                .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(routes);

        foreach (var step in SetupChecklist.For(new DeploymentPlan(), false, null))
        {
            Assert.True(
                routes.Contains(step.Page),
                $"Step '{step.Title}' sends the operator to '{step.Page}', which no page serves. "
                + $"Known routes: {string.Join(", ", routes.Order())}.");
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static DeploymentPlan PlanWithPrimarySite(ImageReferenceSpec image)
    {
        var plan = new DeploymentPlan();
        plan.Azure.SubscriptionId = "0000";
        plan.Azure.Location = "eastus";
        plan.Azure.ResourceGroup = "lab-rg";
        plan.Identity.DomainName = "contoso.local";
        plan.Identity.AdminUsername = "labadmin";

        plan.Servers.Add(RoleCatalog.CreateDefault(ServerRole.DomainController, "lab"));

        var ps = RoleCatalog.CreateDefault(ServerRole.SccmPrimarySite, "lab");
        ps.Image = image;
        plan.Servers.Add(ps);

        return plan;
    }

    /// <summary>
    /// The checklist UI marks the next step by matching on <see cref="SetupStep.Title"/>, because
    /// matching on instance identity broke silently: the component rebuilt the list per access,
    /// so the highlighted step was never the same object as any rendered step and nothing was
    /// ever highlighted. Unique titles are what makes the replacement safe.
    /// </summary>
    [Fact]
    public void Step_titles_are_unique_so_the_ui_can_match_on_them()
    {
        var plans = new[]
        {
            new DeploymentPlan(),
            PlanWithPrimarySite(ImageDefaults.SqlServer)
        };

        foreach (var plan in plans)
        {
            foreach (var signedIn in new[] { false, true })
            {
                var steps = SetupChecklist.For(plan, signedIn, "P@ssw0rd-for-the-lab-1");
                var titles = steps.Select(s => s.Title).ToList();

                Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());

                var next = SetupChecklist.NextStep(steps);
                if (next is not null)
                {
                    Assert.Single(steps, s => s.Title == next.Title);
                }
            }
        }
    }

    /// <summary>
    /// Roles whose DSC configuration reaches the public internet are recorded in the role table,
    /// because the download happens inside the VM after ARM has already reported success. In a
    /// disconnected enclave that reads as "the VM built and then nothing happened".
    /// </summary>
    [Fact]
    public void Roles_that_download_from_the_internet_are_flagged_for_a_custom_cloud()
    {
        foreach (var role in new[] { ServerRole.Exchange, ServerRole.SccmPrimarySite })
        {
            var definition = RoleCatalog.Get(role);
            Assert.NotNull(definition.InternetDownload);

            var plan = PlanWithRole(role);

            plan.Azure.Cloud = AzureCloud.Public;
            Assert.DoesNotContain(Validate(plan), i =>
                i.Message.Contains("disconnected environment", StringComparison.Ordinal));

            plan.Azure.Cloud = AzureCloud.Custom;
            var warning = Assert.Single(Validate(plan), i =>
                i.Message.Contains("disconnected environment", StringComparison.Ordinal));
            Assert.Equal(ValidationSeverity.Warning, warning.Severity);
            Assert.Contains(definition.InternetDownload!, warning.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Roles that install only from the image and the staged package must not be flagged, or the
    /// air-gap warning becomes noise that gets ignored.
    /// </summary>
    [Fact]
    public void Roles_that_install_offline_are_not_flagged()
    {
        foreach (var role in new[] { ServerRole.DomainController, ServerRole.MemberServer, ServerRole.Workstation })
        {
            Assert.Null(RoleCatalog.Get(role).InternetDownload);

            var plan = PlanWithRole(role);
            plan.Azure.Cloud = AzureCloud.Custom;

            Assert.DoesNotContain(Validate(plan), i =>
                i.Message.Contains("disconnected environment", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The role reads as a generic "Exchange Server" and its DSC token still says Exchange2019,
    /// while InstallExchange.ps1 pins the ISO and takes no parameters - so the plan has to say
    /// which release actually gets installed. It is Exchange Server SE now; it used to be 2016
    /// CU12, which was out of support and was never supported on the Windows Server 2022 image
    /// this role deploys.
    /// </summary>
    [Fact]
    public void The_exchange_role_says_which_release_it_installs()
    {
        var warning = Assert.Single(Validate(PlanWithRole(ServerRole.Exchange)), i =>
            i.Message.Contains("Exchange Server SE", StringComparison.Ordinal));

        Assert.Equal(ValidationSeverity.Warning, warning.Severity);

        // The old claim must be gone, not merely joined by the new one.
        Assert.DoesNotContain(Validate(PlanWithRole(ServerRole.Exchange)), i =>
            i.Message.Contains("Exchange Server 2016", StringComparison.Ordinal));

        Assert.DoesNotContain(Validate(PlanWithRole(ServerRole.MemberServer)), i =>
            i.Message.Contains("Exchange Server SE", StringComparison.Ordinal));
    }

    private static DeploymentPlan PlanWithRole(ServerRole role)
    {
        var plan = new DeploymentPlan();
        plan.Servers.Clear();
        plan.Servers.Add(RoleCatalog.CreateDefault(ServerRole.DomainController, "lab"));

        if (role != ServerRole.DomainController)
        {
            plan.Servers.Add(RoleCatalog.CreateDefault(role, "lab"));
        }

        return plan;
    }

    private static IReadOnlyList<ValidationIssue> Validate(DeploymentPlan plan)
    {
        using var secrets = new DeploymentSecrets(PasswordGenerator.Generate("labadmin"));
        return new DeploymentPlanValidator().Validate(plan, secrets).Issues;
    }
}
