namespace IaaSBuilder.Core.Deployment;

/// <summary>
/// The installer files a VM may fetch from <c>ArtifactSpec.InstallMediaLocation</c>.
/// </summary>
/// <remarks>
/// <para>
/// The DSC package downloads all of these from fixed addresses on the public internet while a
/// configuration runs inside the VM. An operator who cannot reach those addresses stages the files
/// somewhere they can reach instead, and the scripts look for these exact names.
/// </para>
/// <para>
/// The names live here so that the plan, the web UI, the README and the tests all describe the
/// same list. The authoritative copy is the DSC package itself - these are the strings the
/// PowerShell passes to <c>Save-InstallMedia</c> - so <c>DscInstallMediaTests</c> reads the package
/// and asserts the two agree, rather than trusting this list to stay true on its own.
/// </para>
/// </remarks>
public static class DscInstallMedia
{
    /// <summary>File names, in the order an operator is most likely to need them.</summary>
    public static readonly string[] FileNames =
    [
        "CMCB.exe",
        "adksetup.exe",
        "adksetupwinpe.exe",
        "ExchangeServerSE-x64.iso",
        "UcmaRuntimeSetup.exe",
        "vcredist2012_x64.exe",
        "vcredist2013_x64.exe",
        "rewrite_amd64_en-US.msi",
        "MSFTBaseline.zip",
        "DoDSTIGs.zip"
    ];

    /// <summary>What each file is for, keyed by file name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CMCB.exe"] = "Configuration Manager baseline installer",
            ["adksetup.exe"] = "Windows ADK 10.1.26100.2454",
            ["adksetupwinpe.exe"] = "Windows PE add-on, matching that ADK",
            ["ExchangeServerSE-x64.iso"] = "Exchange Server SE, about 6 GB",
            ["UcmaRuntimeSetup.exe"] = "UCMA 4.0 runtime, required by Exchange",
            ["vcredist2012_x64.exe"] = "Visual C++ 2012 Update 4 redistributable",
            ["vcredist2013_x64.exe"] = "Visual C++ 2013 redistributable",
            ["rewrite_amd64_en-US.msi"] = "IIS URL Rewrite 2.1, required by Exchange",
            ["MSFTBaseline.zip"] = "Microsoft security baseline GPOs",
            ["DoDSTIGs.zip"] = "DISA STIG GPO package from public.cyber.mil"
        };

    /// <summary>
    /// The files a plan will actually ask for, so an operator is not told to stage Exchange media
    /// for a lab that has no Exchange server in it.
    /// </summary>
    public static IEnumerable<string> RequiredFor(Models.DeploymentPlan plan)
    {
        var roles = plan.Servers
            .Where(s => s.Enabled)
            .Select(s => s.Role)
            .ToHashSet();

        if (roles.Contains(Models.ServerRole.SccmPrimarySite))
        {
            yield return "CMCB.exe";
            yield return "adksetup.exe";
            yield return "adksetupwinpe.exe";
        }

        if (roles.Contains(Models.ServerRole.Exchange))
        {
            yield return "ExchangeServerSE-x64.iso";
            yield return "UcmaRuntimeSetup.exe";
            yield return "vcredist2012_x64.exe";
            yield return "vcredist2013_x64.exe";
            yield return "rewrite_amd64_en-US.msi";
        }

        // These two come from the hardening toggles rather than from a role, and they are applied
        // by the domain controller configuration.
        if (plan.Hardening.ApplyMicrosoftBaseline)
        {
            yield return "MSFTBaseline.zip";
        }

        if (plan.Hardening.ApplyStig)
        {
            yield return "DoDSTIGs.zip";
        }
    }
}
