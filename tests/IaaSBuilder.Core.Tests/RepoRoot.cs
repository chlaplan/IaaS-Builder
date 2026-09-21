namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Locates the repository root so tests can run against the real Templates/ and DSC/ assets
/// rather than fixtures, which is the point: these assets are what actually ship.
/// </summary>
public static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            // Probe for the shape of the repository rather than a specific solution file name,
            // so renaming the solution cannot silently break every asset-backed test.
            if (Directory.Exists(System.IO.Path.Combine(directory.FullName, "Templates")) &&
                Directory.Exists(System.IO.Path.Combine(directory.FullName, "DSC")) &&
                (directory.EnumerateFiles("*.slnx").Any() || directory.EnumerateFiles("*.sln").Any()))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    public static string DscPackage => System.IO.Path.Combine(Path, "DSC", "Configuration.zip");
}
