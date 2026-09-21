using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Guards the contents of DSC/Configuration.zip.
/// </summary>
/// <remarks>
/// The package is a binary blob in the repository, so nothing about it is visible in a diff
/// and nothing in it is compiled. A real deployment failed inside it - the domain controller
/// registered two scheduled tasks under a domain account before the forest existed, which
/// Task Scheduler rejects with "No mapping between account names and security IDs was done"
/// - and the only way to see that was to extract the archive by hand. These tests read the
/// archive so that the same class of defect shows up in the suite instead.
/// </remarks>
public class DscPackageContentTests
{
    private static string ReadEntry(string entryName)
    {
        using var archive = ZipFile.OpenRead(RepoRoot.DscPackage);
        var entry = archive.GetEntry(entryName);
        Assert.True(entry is not null, $"'{entryName}' is missing from the DSC package.");
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static IEnumerable<(string Name, string Text)> Configurations()
    {
        using var archive = ZipFile.OpenRead(RepoRoot.DscPackage);
        foreach (var entry in archive.Entries)
        {
            // Top-level role scripts only - the bundled modules ship their own examples.
            if (entry.FullName.Contains('/') ||
                !entry.FullName.EndsWith("Configuration.ps1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            yield return (entry.FullName, reader.ReadToEnd());
        }
    }

    /// <summary>
    /// Returns the body of every <c>ScheduledTask</c> resource declared in a configuration.
    /// </summary>
    private static List<string> ScheduledTaskBlocks(string text)
    {
        var blocks = new List<string>();
        foreach (Match match in Regex.Matches(text, @"^[ \t]*ScheduledTask[ \t]+\w+", RegexOptions.Multiline))
        {
            var open = text.IndexOf('{', match.Index);
            if (open < 0)
            {
                continue;
            }

            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        blocks.Add(text[match.Index..(i + 1)]);
                        break;
                    }
                }
            }
        }

        return blocks;
    }

    /// <summary>
    /// The defect that broke a real deployment, stated generally rather than as a match on
    /// the two tasks that happened to be wrong. A scheduled task that runs as a domain
    /// account cannot be registered until that account resolves to a SID, which means the
    /// resource has to be ordered after the domain exists or the machine has joined it.
    /// </summary>
    [Fact]
    public void Every_scheduled_task_running_as_a_domain_account_is_ordered_after_the_domain()
    {
        var offenders = new List<string>();

        foreach (var (name, text) in Configurations())
        {
            foreach (var block in ScheduledTaskBlocks(text))
            {
                if (!block.Contains("ExecuteAsCredential", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!block.Contains("DependsOn", StringComparison.OrdinalIgnoreCase))
                {
                    var resource = Regex.Match(block, @"ScheduledTask[ \t]+(\w+)").Groups[1].Value;
                    offenders.Add($"{name}:{resource}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These scheduled tasks run as a domain account but declare no DependsOn, so the " +
            "LCM may register them before the account exists: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Both hardening switches default to off, yet the tasks used to be registered either
    /// way - so an ordinary lab failed while trying to schedule scripts that begin with an
    /// "if enabled" test and would have done nothing.
    /// </summary>
    [Fact]
    public void The_domain_controller_only_schedules_hardening_when_it_was_asked_for()
    {
        var text = ReadEntry("DCConfiguration.ps1");

        Assert.Contains("if ($STIG -eq 'true')", text);
        Assert.Contains("if ($MSFTBaseline -eq 'true')", text);

        foreach (var block in ScheduledTaskBlocks(text))
        {
            Assert.Contains("DependsOn", block);
        }
    }

    /// <summary>
    /// Task Scheduler resolves a principal with LookupAccountName, which wants the
    /// down-level "NETBIOS\user" form rather than "contoso.com\user".
    /// </summary>
    [Fact]
    public void The_domain_controller_schedules_tasks_under_a_netbios_principal()
    {
        var text = ReadEntry("DCConfiguration.ps1");

        Assert.Contains("$TaskCreds = New-Object System.Management.Automation.PSCredential (\"$DName\\", text);

        foreach (var block in ScheduledTaskBlocks(text))
        {
            Assert.Contains("ExecuteAsCredential = $TaskCreds", block);
        }
    }

    /// <summary>
    /// Set-WmiInstance is deprecated and does not exist in PowerShell 6 and later. Microsoft's
    /// own maintained equivalent of this resource, ComputerManagementDsc\VirtualMemory, uses
    /// New-CimInstance instead.
    /// </summary>
    [Fact]
    public void The_helper_module_does_not_use_the_deprecated_wmi_cmdlets()
    {
        var text = ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1");

        Assert.DoesNotContain("Set-WmiInstance", text);
    }

    /// <summary>
    /// A page file size is a convenience - Azure Windows images already have one - so it must
    /// never be able to fail an entire build. It also must not re-submit every writable
    /// property of Win32_ComputerSystem by handing the whole instance back to Set-CimInstance.
    /// </summary>
    [Fact]
    public void Setting_the_page_file_cannot_fail_the_build()
    {
        var text = ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1");

        var start = text.IndexOf("class SetCustomPagingFile", StringComparison.Ordinal);
        Assert.True(start >= 0, "SetCustomPagingFile is missing from TemplateHelpDSC.");

        var end = text.IndexOf("class SetupDomain", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of SetCustomPagingFile.");

        var body = text[start..end];

        Assert.Contains("try", body);
        Assert.Contains("catch", body);
        Assert.Contains("-ErrorAction Stop", body);
        Assert.Contains("New-CimInstance", body);
        Assert.DoesNotContain("set-ciminstance $currentstatus", body);
    }
}
