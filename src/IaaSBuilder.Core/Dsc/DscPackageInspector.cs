using System.IO.Compression;

namespace IaaSBuilder.Core.Dsc;

/// <summary>
/// Reads the configuration names out of a DSC package.
/// </summary>
/// <remarks>
/// The ARM templates derive their DSC extension settings as
/// <c>concat(role, 'Configuration.ps1\Configuration')</c>, so a role token that has no
/// matching script in Configuration.zip produces a VM that builds successfully and then
/// silently fails to configure. Inspecting the package up front turns that into a
/// validation error. The legacy form's "Domain Join" role option was exactly this bug:
/// the DSC package only ever contained <c>JoinDomainConfiguration.ps1</c>.
/// </remarks>
public static class DscPackageInspector
{
    private const string Suffix = "Configuration.ps1";

    /// <summary>
    /// Returns the available role tokens, e.g. "DC", "AddDC", "JoinDomain".
    /// Returns an empty set when the package is missing, so validation degrades to a
    /// warning-free pass rather than blocking an otherwise valid offline run.
    /// </summary>
    public static ISet<string> GetAvailableRoleTokens(string packagePath)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(packagePath))
        {
            return tokens;
        }

        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && name.Length > Suffix.Length)
            {
                tokens.Add(name[..^Suffix.Length]);
            }
        }

        return tokens;
    }
}
