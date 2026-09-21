namespace IaaSBuilder.Core.Deployment;

/// <summary>
/// The DSC package served from an ordinary HTTPS URL instead of a staged Azure blob.
/// </summary>
/// <remarks>
/// <para>
/// The DSC extension only needs a URL it can reach from inside the VM. Nothing about it requires
/// Azure Storage - that was simply where the original script put the package. Staging it there is
/// what drags in the blob data-plane role that Owner and Contributor do not grant, which is the
/// single most common reason a deployment in this tool fails.
/// </para>
/// <para>
/// Serving the package from the project's own public repository removes that dependency outright:
/// no storage account, no role assignment, no SAS token. An operator whose VMs cannot reach the
/// public internet supplies their own location instead.
/// </para>
/// </remarks>
public static class PublicPackageSource
{
    /// <summary>
    /// The published package, as a direct link to the file rather than to the GitHub page for it.
    /// </summary>
    /// <remarks>
    /// <c>raw.githubusercontent.com</c> serves the bytes; a <c>github.com/.../tree/...</c> or
    /// <c>/blob/...</c> URL serves an HTML page, and the DSC extension would download that page,
    /// fail to open it as a zip, and report a confusing error well after the VM had been built.
    /// The path is case-sensitive - <c>dsc/Configuration.zip</c> is a 404 where
    /// <c>DSC/Configuration.zip</c> is a 200 - which is why the file name is passed to the
    /// template separately rather than being assumed to be lowercase.
    /// </remarks>
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/chlaplan/IaaS-Builder/master/DSC/Configuration.zip";

    /// <summary>The relative path the templates assume when the package is staged in Azure Storage.</summary>
    public const string StagedPackagePath = "dsc/Configuration.zip";

    /// <summary>
    /// Splits a direct package URL into the base URI and file name the templates combine.
    /// </summary>
    /// <remarks>
    /// The templates build the download as <c>Uri(_artifactsLocation, concat(dscPackagePath, sas))</c>.
    /// Splitting at the last slash - so the folder stays in the base and only the file name is the
    /// relative part - keeps the host's own capitalisation intact. Rebuilding the path from a
    /// hard-coded lowercase constant is what breaks on case-sensitive hosts.
    /// </remarks>
    public static bool TrySplit(string? url, out string baseUri, out string packagePath)
    {
        baseUri = "";
        packagePath = "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        var path = parsed.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == path.Length - 1)
        {
            // A bare host, or a URL ending in a slash: there is no file to download.
            return false;
        }

        var fileName = path[(lastSlash + 1)..];

        var builder = new UriBuilder(parsed)
        {
            Path = path[..(lastSlash + 1)],
            Query = string.Empty,
            Fragment = string.Empty
        };

        baseUri = builder.Uri.ToString();
        packagePath = fileName;
        return true;
    }
}
