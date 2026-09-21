using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Guards the download pins and installer plumbing inside DSC/Configuration.zip.
/// </summary>
/// <remarks>
/// Everything in here fails <em>inside the VM</em>, minutes to hours after ARM has reported the
/// deployment a success, so none of it is visible from the portal, the deployment log or a diff of
/// the repository. The package is a binary blob and nothing in it is compiled, which is exactly why
/// an out-of-support ISO, a setup switch Microsoft removed in 2021 and three installers racing each
/// other all survived in it unnoticed.
/// </remarks>
public class DscInstallerPinTests
{
    private static string ReadEntry(string entryName)
    {
        using var archive = ZipFile.OpenRead(RepoRoot.DscPackage);
        var entry = archive.GetEntry(entryName);
        Assert.True(entry is not null, $"'{entryName}' is missing from the DSC package.");
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Strips full-line PowerShell comments so an assertion about code cannot be satisfied - or
    /// broken - by prose. The Exchange script explains in a comment why the old licence switch was
    /// replaced, and a naive search for that switch finds the explanation.
    /// </summary>
    private static string CodeOnly(string text)
    {
        // Block comments must go first. Stripping line comments first deletes every closing "#>"
        // - it is a line starting with '#' - which leaves the opening "<#" unterminated and the
        // comment body intact, so the assertion then reads the prose it was meant to skip.
        var withoutBlocks = Regex.Replace(text, @"<#.*?#>", string.Empty, RegexOptions.Singleline);

        return string.Join('\n', withoutBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    // ---- Exchange -------------------------------------------------------------------------

    /// <summary>
    /// Exchange 2016 and 2019 both left support on 14 October 2025, and 2016 was never supported
    /// on Windows Server 2022 - which is the image the Exchange role deploys, so the previous pin
    /// was an unsupported combination that happened to work.
    /// </summary>
    [Fact]
    public void Exchange_installs_the_release_that_is_still_in_support()
    {
        var text = ReadEntry("InstallExchange.ps1");

        Assert.Contains("ExchangeServerSE-x64.iso", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExchangeServer2016", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExchangeServer2019", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bare <c>/IAcceptExchangeServerLicenseTerms</c> switch was removed in the September 2021
    /// cumulative updates. Setup now refuses to run unless one of the _DiagnosticData variants is
    /// supplied, so moving to a newer ISO without also changing this would have failed immediately.
    /// </summary>
    [Fact]
    public void Exchange_setup_uses_a_licence_switch_that_still_exists()
    {
        var code = CodeOnly(ReadEntry("InstallExchange.ps1"));

        Assert.Contains("/IAcceptExchangeServerLicenseTerms_DiagnosticData", code, StringComparison.Ordinal);

        // The bare form, i.e. not followed by an underscore.
        Assert.False(
            Regex.IsMatch(code, @"/IAcceptExchangeServerLicenseTerms(?!_)"),
            "Exchange setup is still passed the bare licence switch, which setup has rejected since September 2021.");
    }

    /// <summary>
    /// Every installer in this script used to be launched without <c>-Wait</c>, so the UCMA runtime,
    /// the redistributables and Exchange setup all started at once. The scheduled task then exited
    /// immediately and reported success while setup was still running - or failing.
    /// </summary>
    [Fact]
    public void Every_installer_the_exchange_script_launches_is_waited_on()
    {
        var code = CodeOnly(ReadEntry("InstallExchange.ps1"));

        var launches = Regex.Matches(code, @"Start-Process[^\r\n]*");
        Assert.NotEmpty(launches);

        foreach (Match launch in launches)
        {
            Assert.True(
                launch.Value.Contains("-Wait", StringComparison.OrdinalIgnoreCase),
                $"This installer is not waited on, so it races the ones after it: {launch.Value.Trim()}");
        }
    }

    /// <summary>
    /// The Mailbox role needs both the 2012 and the 2013 redistributable. Only the 2013 one was
    /// fetched, and the IIS URL Rewrite module was missing entirely.
    /// </summary>
    [Fact]
    public void Exchange_downloads_every_prerequisite_the_mailbox_role_needs()
    {
        var text = ReadEntry("InstallExchange.ps1");

        Assert.Contains("UcmaRuntimeSetup.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VSU_4/vcredist_x64.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rewrite_amd64_en-US.msi", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Configuration Manager and the ADK ------------------------------------------------

    /// <summary>
    /// The ADK was pinned to fwlink 2026036 / 2022233, which are the Windows 10 1809 ADK. 1809 does
    /// not appear anywhere in the support matrix for any in-support ConfigMgr release. The pair
    /// below is ADK 10.1.26100.2454, which is supported - and the two links must move together,
    /// because the WinPE add-on has to match the ADK it plugs into.
    /// </summary>
    [Fact]
    public void The_adk_pin_is_a_matched_pair_that_configmgr_supports()
    {
        var text = ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1");

        Assert.Contains("linkid=2289980", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linkid=2289981", text, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("linkid=2026036", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("linkid=2022233", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// These loops wait for a file that an adksetup failure will never create. Unbounded, a failed
    /// install spun here until the DSC extension gave up over an hour later with nothing in the
    /// error about the ADK at all.
    /// </summary>
    [Fact]
    public void The_adk_install_waits_are_bounded()
    {
        var text = ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1");

        var waits = Regex.Matches(text, @"while\(!\(Test-Path \$adkinstallpath\)[^\r\n]*");
        Assert.NotEmpty(waits);

        foreach (Match wait in waits)
        {
            Assert.True(
                wait.Value.Contains("$adkattempt", StringComparison.Ordinal),
                $"This wait has no attempt limit, so a failed ADK install hangs the whole run: {wait.Value.Trim()}");
        }

        // A bound with no report is just a quieter hang.
        Assert.Contains("did not appear at", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fallback that installs a different baseline from the one the rest of the package expects is
    /// worse than no fallback. This one was hard-coded to ConfigMgr 2002 over plain HTTP.
    /// </summary>
    [Fact]
    public void Configuration_manager_is_downloaded_from_one_place_over_https()
    {
        var text = ReadEntry("InstallAndUpdateSCCM.ps1");

        Assert.DoesNotContain("MEM_Configmgr_2002", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SC_Configmgr_SCEP_1902", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linkid=2093192", text, StringComparison.OrdinalIgnoreCase);
    }
}
