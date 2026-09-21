using IaaSBuilder.Core;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The install media location is read for the first time by a PowerShell script running inside a
/// VM, minutes to hours after ARM has reported the deployment successful. Nothing before that point
/// looks at it, so a malformed value costs a whole lab build to discover. These rules are the only
/// thing that turns that into a red box on a form.
/// </summary>
public class InstallMediaValidationTests
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

    private static List<ValidationIssue> For(DeploymentPlan plan, string path) =>
        Validate(plan).Issues.Where(i => i.Path == path).ToList();

    [Fact]
    public void A_new_plan_downloads_from_microsoft_as_before() =>
        Assert.Equal(string.Empty, PlanFactory.CreateDefault().Artifacts.InstallMediaLocation);

    [Fact]
    public void Leaving_it_blank_says_nothing()
    {
        var plan = ValidPlan();
        Assert.Empty(For(plan, "artifacts.installMediaLocation"));
    }

    [Theory]
    [InlineData("https://media.contoso.local/installers")]
    [InlineData("http://media.contoso.local/installers")]
    [InlineData(@"\\fs01\media\installers")]
    [InlineData(@"D:\installers")]
    [InlineData("D:/installers")]
    public void A_usable_location_is_accepted(string location)
    {
        var plan = ValidPlan();
        plan.Artifacts.InstallMediaLocation = location;

        Assert.Empty(For(plan, "artifacts.installMediaLocation"));
    }

    /// <summary>
    /// A relative path means different things to different roles: the scheduled-task scripts run
    /// from <c>C:\Windows\temp\ProvisionScript</c> and the DSC extension from its own package
    /// folder, so a path like <c>installers</c> resolves somewhere different on every call site.
    /// </summary>
    [Theory]
    [InlineData("installers")]
    [InlineData(@".\installers")]
    [InlineData("/mnt/media")]
    [InlineData("fs01/media")]
    public void A_location_that_is_not_anchored_is_refused(string location)
    {
        var plan = ValidPlan();
        plan.Artifacts.InstallMediaLocation = location;

        var issues = For(plan, "artifacts.installMediaLocation");
        Assert.Single(issues);
        Assert.Equal(ValidationSeverity.Error, issues[0].Severity);
    }

    /// <summary>
    /// The file names are appended to this value, so pointing at one installer produces requests
    /// for <c>...\CMCB.exe\adksetup.exe</c> - which fails with a path error that says nothing about
    /// the real mistake.
    /// </summary>
    [Theory]
    [InlineData(@"\\fs01\media\CMCB.exe")]
    [InlineData("https://media.contoso.local/ExchangeServerSE-x64.iso")]
    [InlineData(@"D:\media\MSFTBaseline.zip")]
    [InlineData(@"D:\media\rewrite_amd64_en-US.msi")]
    public void Pointing_at_a_file_rather_than_the_folder_is_refused(string location)
    {
        var plan = ValidPlan();
        plan.Artifacts.InstallMediaLocation = location;

        var issues = For(plan, "artifacts.installMediaLocation");
        Assert.Single(issues);
        Assert.Equal(ValidationSeverity.Error, issues[0].Severity);
        Assert.Contains("folder", issues[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Always a half-finished edit, and silently does nothing at all - the fall-back only applies
    /// when there is a location to fall back from.
    /// </summary>
    [Fact]
    public void Falling_back_with_nowhere_to_fall_back_from_is_reported()
    {
        var plan = ValidPlan();
        plan.Artifacts.InstallMediaFallbackToInternet = true;

        var issues = For(plan, "artifacts.installMediaFallbackToInternet");
        Assert.Single(issues);
        Assert.Equal(ValidationSeverity.Warning, issues[0].Severity);
    }

    [Fact]
    public void Falling_back_from_a_real_location_is_fine()
    {
        var plan = ValidPlan();
        plan.Artifacts.InstallMediaLocation = @"\\fs01\media\installers";
        plan.Artifacts.InstallMediaFallbackToInternet = true;

        Assert.Empty(For(plan, "artifacts.installMediaFallbackToInternet"));
    }

    // ---- The air-gap warning this setting exists to answer ----------------------------------

    private static DeploymentPlan AirGappedExchangeLab()
    {
        var plan = ValidPlan();
        plan.Azure.Cloud = AzureCloud.Custom;
        PlanFactory.AddServer(plan, ServerRole.Exchange);
        return plan;
    }

    [Fact]
    public void A_disconnected_lab_is_warned_that_its_roles_download_installers()
    {
        var plan = AirGappedExchangeLab();

        // "downloads" alone also matches the Exchange advisory about the 6 GB ISO, which has
        // nothing to do with air-gapping and is emitted in every cloud.
        var warnings = Validate(plan).Issues
            .Where(i => i.Path.EndsWith(".role", StringComparison.Ordinal)
                        && i.Message.Contains("disconnected environment", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains("install media location", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Once the operator has answered the warning, it has to stop. A warning that stays up after
    /// it has been acted on is the reason people stop reading warnings.
    /// </summary>
    [Fact]
    public void Naming_a_media_location_silences_that_warning()
    {
        var plan = AirGappedExchangeLab();
        plan.Artifacts.InstallMediaLocation = @"\\fs01\media\installers";

        Assert.DoesNotContain(Validate(plan).Issues, i =>
            i.Path.EndsWith(".role", StringComparison.Ordinal)
            && i.Message.Contains("disconnected environment", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The manifest shown to the operator -------------------------------------------------

    /// <summary>
    /// Telling somebody building a plain domain controller to go and find a 6 GB Exchange ISO is
    /// how a staging list gets ignored.
    /// </summary>
    [Fact]
    public void A_plain_domain_controller_needs_no_staged_media() =>
        Assert.Empty(DscInstallMedia.RequiredFor(ValidPlan()));

    [Fact]
    public void An_exchange_lab_is_told_to_stage_the_exchange_media()
    {
        var files = DscInstallMedia.RequiredFor(AirGappedExchangeLab()).ToList();

        Assert.Contains("ExchangeServerSE-x64.iso", files);
        Assert.Contains("UcmaRuntimeSetup.exe", files);
        Assert.DoesNotContain("CMCB.exe", files);
    }

    [Fact]
    public void A_configuration_manager_lab_is_told_to_stage_the_adk()
    {
        var plan = ValidPlan();
        PlanFactory.AddServer(plan, ServerRole.SccmPrimarySite);

        var files = DscInstallMedia.RequiredFor(plan).ToList();

        Assert.Contains("CMCB.exe", files);
        Assert.Contains("adksetup.exe", files);
        Assert.Contains("adksetupwinpe.exe", files);
    }

    /// <summary>
    /// A disabled server is not deployed, so its media is not needed. Listing it sends the operator
    /// after a download nothing will ever read.
    /// </summary>
    [Fact]
    public void A_disabled_server_asks_for_nothing()
    {
        var plan = AirGappedExchangeLab();
        foreach (var server in plan.Servers) server.Enabled = false;

        Assert.Empty(DscInstallMedia.RequiredFor(plan));
    }

    [Fact]
    public void The_hardening_switches_bring_their_own_media()
    {
        var plan = ValidPlan();
        plan.Hardening.ApplyStig = true;
        plan.Hardening.ApplyMicrosoftBaseline = true;

        var files = DscInstallMedia.RequiredFor(plan).ToList();

        Assert.Contains("DoDSTIGs.zip", files);
        Assert.Contains("MSFTBaseline.zip", files);
    }

    /// <summary>
    /// Guards the guard. Every name the UI can list must have prose beside it, or the operator gets
    /// a bare file name and no idea what to go and find.
    /// </summary>
    [Fact]
    public void Every_file_in_the_manifest_is_described()
    {
        Assert.NotEmpty(DscInstallMedia.FileNames);

        foreach (var name in DscInstallMedia.FileNames)
        {
            Assert.True(DscInstallMedia.Descriptions.ContainsKey(name),
                $"'{name}' has no description, so the staging list would show a bare file name.");
        }

        Assert.Equal(DscInstallMedia.FileNames.Length, DscInstallMedia.Descriptions.Count);
    }
}
