using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using IaaSBuilder.Core.Deployment;
using Xunit;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Guards the configurable install media path through DSC/Configuration.zip.
/// </summary>
/// <remarks>
/// <para>
/// The value travels a long way with no type checking anywhere along it: an ARM parameter, a DSC
/// extension <c>Properties</c> key, a configuration parameter, a <c>File</c> resource that writes
/// <c>C:\InstallMedia.txt</c>, and finally a PowerShell function every download site calls. A break
/// at any link is silent - the download simply goes to Microsoft as before, which looks like
/// success on a connected network and hangs for an hour on a disconnected one.
/// </para>
/// <para>
/// So these read the real package rather than trusting it. The package is a binary blob; a diff of
/// the repository shows one changed zip and nothing about what is inside it.
/// </para>
/// </remarks>
public class DscInstallMediaTests
{
    private static readonly string[] Configurations =
    [
        "AddDCConfiguration.ps1", "ADFSConfiguration.ps1", "CAConfiguration.ps1",
        "ClientConfiguration.ps1", "DCConfiguration.ps1", "DPMPConfiguration.ps1",
        "Exchange2016Configuration.ps1", "Exchange2019Configuration.ps1",
        "ExchangeConfiguration.ps1", "JoinDomainConfiguration.ps1", "PSConfiguration.ps1",
        "SFBConfiguration.ps1", "SPConfiguration.ps1", "SQLConfiguration.ps1"
    ];

    private static string ReadEntry(string entryName)
    {
        using var archive = ZipFile.OpenRead(RepoRoot.DscPackage);
        var entry = archive.GetEntry(entryName);
        Assert.True(entry is not null, $"'{entryName}' is missing from the DSC package.");
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> ReadScripts()
    {
        using var archive = ZipFile.OpenRead(RepoRoot.DscPackage);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0) continue;
            if (!entry.Name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                && !entry.Name.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)) continue;

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            found[entry.FullName] = reader.ReadToEnd();
        }

        return found;
    }

    /// <summary>
    /// Strips PowerShell comments, so prose explaining why a raw download was removed cannot
    /// satisfy - or fail - an assertion about code. Block comments first: stripping line comments
    /// first deletes every closing <c>#&gt;</c> and leaves the comment body behind.
    /// </summary>
    private static string CodeOnly(string text)
    {
        var withoutBlocks = Regex.Replace(text, @"<#.*?#>", string.Empty, RegexOptions.Singleline);
        return string.Join('\n', withoutBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    // ---- The resolver itself ---------------------------------------------------------------

    [Fact]
    public void The_package_carries_the_resolver()
    {
        var text = ReadEntry("InstallMedia.ps1");

        Assert.Contains("function Get-InstallMediaSetting", text, StringComparison.Ordinal);
        Assert.Contains("function Get-InstallMediaUri", text, StringComparison.Ordinal);
        Assert.Contains("function Save-InstallMedia", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scripts and the DSC configurations agree on this path, but they are written in different
    /// files and nothing joins them. If they drift, the setting is written and never read, and
    /// every download silently goes to Microsoft instead.
    /// </summary>
    [Fact]
    public void The_resolver_and_the_configurations_agree_on_where_the_setting_lives()
    {
        Assert.Contains(@"C:\InstallMedia.txt", ReadEntry("InstallMedia.ps1"), StringComparison.OrdinalIgnoreCase);

        foreach (var name in Configurations)
        {
            Assert.True(
                ReadEntry(name).Contains(@"C:\InstallMedia.txt", StringComparison.OrdinalIgnoreCase),
                $"{name} does not write C:\\InstallMedia.txt, so media staged for this lab would be ignored on that role.");
        }
    }

    /// <summary>
    /// <c>TemplateHelpDSC.psm1</c> installs to the PowerShell module path, away from the package,
    /// so it cannot dot-source the package-root copy and carries its own. Two copies of anything
    /// drift; this is the only thing stopping that being discovered inside a VM.
    /// </summary>
    [Fact]
    public void The_modules_copy_of_the_resolver_matches_the_packages()
    {
        static string Normalise(string text) =>
            Regex.Replace(CodeOnly(text), @"\s+", " ").Trim();

        var shared = Normalise(ReadEntry("InstallMedia.ps1"));
        var module = Normalise(ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1"));

        // The module prepends the region verbatim, so the whole shared body must appear in it.
        Assert.True(
            module.Contains(shared[shared.IndexOf("function Get-InstallMediaSetting", StringComparison.Ordinal)..],
                StringComparison.Ordinal),
            "TemplateHelpDSC.psm1's copy of the install media resolver has drifted from InstallMedia.ps1. " +
            "They must stay identical - the module cannot dot-source the package copy.");
    }

    // ---- Every download goes through it -----------------------------------------------------

    /// <summary>
    /// The point of the whole exercise. A single raw download left behind is a role that ignores
    /// the staged media, and it fails only once that role runs.
    /// </summary>
    [Fact]
    public void No_installer_is_downloaded_without_going_through_the_resolver()
    {
        // TemplateHelpDSC.psm1 hosts a verbatim copy of the resolver, so the fetches inside that
        // copy are the resolver's own. Everything after it must go through Save-InstallMedia.
        var module = CodeOnly(ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1"));
        var resolverEnd = module.IndexOf("[DscResource()]", StringComparison.Ordinal);

        Assert.True(
            module.IndexOf("function Get-InstallMediaSetting", StringComparison.Ordinal) >= 0
            && resolverEnd > 0,
            "Could not locate the copied resolver in TemplateHelpDSC.psm1, so this test cannot tell " +
            "its downloads from any others. Fix the test before trusting it.");

        var resolverLastLine = module[..resolverEnd].Count(c => c == '\n') + 1;

        var offenders = new List<string>();

        foreach (var (path, text) in ReadScripts())
        {
            // The resolver is the one place allowed to fetch bytes.
            if (path.Equals("InstallMedia.ps1", StringComparison.OrdinalIgnoreCase)) continue;

            var isModule = path.Equals("TemplateHelpDSC/TemplateHelpDSC.psm1", StringComparison.OrdinalIgnoreCase);

            // Bundled third-party modules and their tests are not ours to rewrite, and none of them
            // download installer media - they are DSC resources for IIS, SQL, AD and so on.
            if (path.Contains('/') && !isModule) continue;

            var code = CodeOnly(text);

            // Anchored to real download calls. A bare "WebClient" also matches the Windows feature
            // named "Web-Client-Auth" as the DSC resource id WebClientAuth, which is how an earlier
            // version of this test reported four downloads in the Exchange configurations that do
            // not exist.
            const string downloads =
                @"\bInvoke-WebRequest\b|\bStart-BitsTransfer\b|New-Object\s+(?:System\.Net\.)?WebClient|\[System\.Net\.WebClient\]";

            foreach (Match match in Regex.Matches(code, downloads))
            {
                var line = code[..match.Index].Count(c => c == '\n') + 1;

                if (isModule && line <= resolverLastLine) continue;

                // STIGDC.ps1 reads an HTML page to find the current STIG link. That is a lookup,
                // not a media download, and it is already skipped when the file is mirrored.
                if (path.Equals("STIGDC.ps1", StringComparison.OrdinalIgnoreCase)
                    && code.Contains("public.cyber.mil", StringComparison.OrdinalIgnoreCase)) continue;

                offenders.Add($"{path} line {line}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These download installer media directly instead of calling Save-InstallMedia, so a " +
            "staged install media location would be ignored:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The manifest shown in the UI and the README is only useful if it is what the scripts
    /// actually ask for. A name that differs by a character produces a file the VM walks past.
    /// </summary>
    [Theory]
    [InlineData("adksetup.exe")]
    [InlineData("adksetupwinpe.exe")]
    [InlineData("ExchangeServerSE-x64.iso")]
    [InlineData("UcmaRuntimeSetup.exe")]
    [InlineData("vcredist2012_x64.exe")]
    [InlineData("vcredist2013_x64.exe")]
    [InlineData("rewrite_amd64_en-US.msi")]
    [InlineData("MSFTBaseline.zip")]
    [InlineData("DoDSTIGs.zip")]
    public void Each_staged_file_name_is_one_the_package_asks_for(string fileName)
    {
        Assert.Contains(fileName, DscInstallMedia.FileNames);

        var found = ReadScripts().Any(kv =>
            !kv.Key.Contains('/') || kv.Key.StartsWith("TemplateHelpDSC/", StringComparison.OrdinalIgnoreCase)
                ? kv.Value.Contains(fileName, StringComparison.OrdinalIgnoreCase)
                : false);

        Assert.True(found,
            $"No script in the package mentions '{fileName}', so an operator told to stage it would " +
            "be staging a file nothing ever looks for.");
    }

    /// <summary>
    /// ConfigMgr is the exception: its file name is built from the <c>$CM</c> parameter rather than
    /// written out, so it is pinned against that parameter's default instead of by a plain search.
    /// </summary>
    [Fact]
    public void The_configuration_manager_file_name_matches_the_parameter_it_is_built_from()
    {
        Assert.Contains("CMCB.exe", DscInstallMedia.FileNames);

        Assert.Matches(@"\$CM\s*=\s*[""']CMCB[""']", ReadEntry("PSConfiguration.ps1"));
        Assert.Contains(@"Save-InstallMedia -FileName ""$_CM.exe""",
            ReadEntry("TemplateHelpDSC/TemplateHelpDSC.psm1"), StringComparison.Ordinal);
    }

    // ---- The transport ----------------------------------------------------------------------

    /// <summary>
    /// The DSC extension splats the template's Properties block onto the configuration function, so
    /// a key any one configuration does not declare fails the entire run - not just the role that
    /// needed it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigurationNames))]
    public void Every_configuration_accepts_the_install_media_parameters(string name)
    {
        var text = ReadEntry(name);

        Assert.Contains("$InstallMediaLocation", text, StringComparison.Ordinal);
        Assert.Contains("$InstallMediaFallback", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing the setting is what carries it to the scheduled-task scripts and the class
    /// resources, which have no parameter channel in common with the configuration.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigurationNames))]
    public void Every_configuration_writes_the_setting_to_disk(string name)
    {
        Assert.Contains("File InstallMediaSetting", ReadEntry(name), StringComparison.Ordinal);
    }

    public static TheoryData<string> ConfigurationNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Configurations) data.Add(name);
        return data;
    }

    /// <summary>
    /// Strict by default. In an enclave a silent fall-back to the internet turns a precise
    /// "stage this file" message into an hour-long hang that never mentions media.
    /// </summary>
    [Fact]
    public void Falling_back_to_the_internet_is_off_unless_it_is_asked_for()
    {
        foreach (var name in Configurations)
        {
            Assert.Matches(@"\$InstallMediaFallback\s*=\s*'false'", ReadEntry(name));
        }
    }

    /// <summary>
    /// The STIG script scrapes public.cyber.mil for a download link. That scrape is the slowest and
    /// least reliable thing in the package, and it is pointless when the file is already mirrored.
    /// </summary>
    [Fact]
    public void The_stig_script_does_not_scrape_when_the_media_is_mirrored()
    {
        var code = CodeOnly(ReadEntry("STIGDC.ps1"));

        // The mirror is consulted by Get-InstallMediaUri, not by Save-InstallMedia. Save- is
        // called at the end either way, so ordering against it proves nothing - an earlier version
        // of this test did exactly that and failed against a correct script.
        var lookup = code.IndexOf("Get-InstallMediaUri", StringComparison.Ordinal);
        var scrape = code.IndexOf("public.cyber.mil", StringComparison.OrdinalIgnoreCase);

        Assert.True(lookup >= 0, "STIGDC.ps1 no longer consults the staged media location at all.");
        Assert.True(scrape < 0 || lookup < scrape,
            "STIGDC.ps1 scrapes public.cyber.mil before consulting the staged media, so a " +
            "disconnected lab still waits on a site it cannot reach.");
    }
}
