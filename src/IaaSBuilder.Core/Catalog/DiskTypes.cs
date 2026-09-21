namespace IaaSBuilder.Core.Catalog;

/// <summary>
/// Which managed disk types a VM size can actually use.
/// </summary>
/// <remarks>
/// <para>
/// Not every VM size can attach Premium SSD. The A-series, the original D/DS v1 and v2 families
/// and a number of older sizes cannot, and Azure rejects the deployment outright:
/// </para>
/// <code>
/// InvalidParameter: Requested operation cannot be performed because the VM size
/// Standard_A2_v2 does not support the storage account type Premium_LRS of disk
/// 'lablabdc01-OsDisk'.
/// </code>
/// <para>
/// That arrives from the Compute resource provider several minutes into a build, after the
/// network, the resource group and possibly other VMs already exist. The signal needed to prevent
/// it - the <c>PremiumIO</c> capability - is in the same SKU data the catalog already downloads to
/// fill the size dropdown, so there is no reason for anyone to discover this from a failed
/// deployment.
/// </para>
/// <para>
/// This lives in Core rather than in the page because three things need the same answer: the disk
/// dropdown, the auto-correction applied when the size changes, and the validator backstop for
/// plan files that were hand-edited or written before this check existed.
/// </para>
/// </remarks>
public static class DiskTypes
{
    public const string PremiumSsd = "Premium_LRS";
    public const string PremiumSsdV2 = "PremiumV2_LRS";
    public const string StandardSsd = "StandardSSD_LRS";
    public const string StandardHdd = "Standard_LRS";

    /// <summary>Every disk type the tool offers, best first.</summary>
    public static readonly string[] All = [PremiumSsd, PremiumSsdV2, StandardSsd, StandardHdd];

    /// <summary>
    /// The disk types that need the size to advertise <c>PremiumIO</c>. Premium SSD v2 is included
    /// because it has the same requirement plus further constraints of its own.
    /// </summary>
    private static readonly string[] NeedPremiumCapableSize = [PremiumSsd, PremiumSsdV2];

    public static bool RequiresPremiumCapableSize(string? diskType) =>
        NeedPremiumCapableSize.Contains(diskType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The disk types <paramref name="size"/> can use. An unknown size - one missing from the
    /// catalog, or a catalog snapshot taken before the capability was captured - returns
    /// everything, because refusing a choice on absent metadata is worse than letting Azure
    /// answer.
    /// </summary>
    public static IReadOnlyList<string> For(VmSizeInfo? size) =>
        size is null || !size.KnownToRejectPremium
            ? All
            : [StandardSsd, StandardHdd];

    public static bool IsSupported(VmSizeInfo? size, string? diskType) =>
        !RequiresPremiumCapableSize(diskType) || size?.KnownToRejectPremium != true;

    /// <summary>
    /// The disk type to move to when the chosen one is no longer usable. Standard SSD, not
    /// Standard HDD: a lab that quietly dropped to spinning disks would be blamed on the tool long
    /// before anyone looked at the disk type.
    /// </summary>
    public static string BestAvailable(VmSizeInfo? size) =>
        For(size).Contains(PremiumSsd) ? PremiumSsd : StandardSsd;
}
