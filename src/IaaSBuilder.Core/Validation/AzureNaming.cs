using System.Text.RegularExpressions;

namespace IaaSBuilder.Core.Validation;

/// <summary>
/// Azure resource naming rules. The legacy tool had none, so an invalid storage account
/// name or an over-long computer name only surfaced as an ARM error minutes into a build.
/// </summary>
public static partial class AzureNaming
{
    /// <summary>Domain-joined Windows computer names are limited to 15 characters.</summary>
    public const int MaxWindowsComputerNameLength = 15;

    [GeneratedRegex("^[a-z0-9]{3,24}$")]
    private static partial Regex StorageAccountName();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}[a-zA-Z0-9]$|^[a-zA-Z0-9]$")]
    private static partial Regex ResourceGroupName();

    [GeneratedRegex(@"^(?!-)[A-Za-z0-9-]{1,15}(?<!-)$")]
    private static partial Regex ComputerName();

    [GeneratedRegex(@"^(?=.{3,63}$)[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex BlobContainerName();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$")]
    private static partial Regex DomainName();

    public static bool IsValidStorageAccountName(string? name) =>
        !string.IsNullOrEmpty(name) && StorageAccountName().IsMatch(name);

    public static bool IsValidResourceGroupName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 90 && ResourceGroupName().IsMatch(name);

    public static bool IsValidComputerName(string? name) =>
        !string.IsNullOrEmpty(name) && ComputerName().IsMatch(name) && !name.All(char.IsDigit);

    /// <summary>
    /// Blob container names: 3-63 characters, lowercase letters, digits and hyphens, no leading,
    /// trailing or consecutive hyphens. Stricter than the storage account rule because containers
    /// do allow hyphens - just not two in a row, which is the part people get wrong.
    /// </summary>
    public static bool IsValidBlobContainerName(string? name) =>
        !string.IsNullOrEmpty(name) && BlobContainerName().IsMatch(name);

    public static bool IsValidDomainName(string? name) =>
        !string.IsNullOrEmpty(name) && DomainName().IsMatch(name);

    /// <summary>NetBIOS label derived from an AD DNS domain name, e.g. contoso.local -> CONTOSO.</summary>
    public static string ToNetBiosName(string domainName)
    {
        var label = domainName.Split('.').FirstOrDefault() ?? domainName;
        return label.Length > 15
            ? label[..15].ToUpperInvariant()
            : label.ToUpperInvariant();
    }
}
