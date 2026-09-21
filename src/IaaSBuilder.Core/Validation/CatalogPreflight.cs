using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Validation;

/// <summary>
/// Checks a plan against the resource catalog: does this region exist, is this VM size offered
/// here, is this image SKU real.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeploymentPlanValidator"/> can only check a plan against itself - that an IP is
/// inside the subnet, that a name is unique. It cannot know that
/// <c>2022-datacenter-azure-edition</c> is not published in a given region. ARM template
/// validation does not reliably catch it either, because a marketplace image reference is only
/// resolved when the VM is actually created. The result is a deployment that fails part way
/// through, after the resource group, the storage account and the network already exist.
/// </para>
/// <para>
/// Every check here is <b>conditional on the catalog actually having data for that scope</b>.
/// An empty catalog, or an offer that was never enumerated, produces no issues at all - saying
/// nothing is much better than crying wolf on a disconnected machine. For the same reason a
/// stale snapshot only ever produces warnings: a genuinely new SKU will not be in an old
/// snapshot, and that must not block a deployment.
/// </para>
/// </remarks>
public static class CatalogPreflight
{
    /// <summary>A snapshot older than this can legitimately be missing new SKUs.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public static IReadOnlyList<ValidationIssue> Check(DeploymentPlan plan, ResourceCatalog? catalog)
    {
        if (catalog is null)
        {
            return [];
        }

        var issues = new List<ValidationIssue>();

        // A stale snapshot is evidence, not proof, so it may only warn.
        var severity = catalog.IsStale(StaleAfter) ? ValidationSeverity.Warning : ValidationSeverity.Error;
        var location = plan.Azure.Location;

        if (string.IsNullOrWhiteSpace(location))
        {
            // The validator already reports the missing region; nothing here can be checked.
            return [];
        }

        CheckLocation(plan, catalog, issues, severity, location);
        CheckServers(plan, catalog, issues, severity, location);
        CheckDedicatedHost(plan, catalog, issues, severity, location);
        CheckAvd(plan, catalog, issues, severity);

        return issues;
    }

    private static void CheckLocation(
        DeploymentPlan plan,
        ResourceCatalog catalog,
        List<ValidationIssue> issues,
        ValidationSeverity severity,
        string location)
    {
        if (catalog.Locations.Count == 0)
        {
            return;
        }

        if (!catalog.Locations.Any(l => Same(l.Name, location)))
        {
            issues.Add(new ValidationIssue(severity, "azure.location",
                $"Region '{location}' is not available to this subscription. " +
                $"Known regions: {Sample(catalog.Locations.Select(l => l.Name))}."));
        }
    }

    private static void CheckServers(
        DeploymentPlan plan,
        ResourceCatalog catalog,
        List<ValidationIssue> issues,
        ValidationSeverity severity,
        string location)
    {
        var sizes = catalog.GetVmSizes(location);
        var index = 0;

        foreach (var server in plan.Servers)
        {
            var position = index++;

            if (!server.Enabled)
            {
                continue;
            }

            var size = sizes.FirstOrDefault(s => Same(s.Name, server.VmSize));

            if (sizes.Count > 0
                && !string.IsNullOrWhiteSpace(server.VmSize)
                && size is null)
            {
                issues.Add(new ValidationIssue(severity, $"servers[{position}].vmSize",
                    $"VM size '{server.VmSize}' is not offered in '{location}'."));
            }

            // Reported as an error even against a stale snapshot, unlike everything else here.
            // Whether a size supports Premium SSD is a fixed property of that size - it does not
            // change the way a region's SKU list does - so an old snapshot is still right about
            // it. Azure rejects the deployment outright, several minutes in, once the network and
            // resource group already exist.
            if (size is not null && !DiskTypes.IsSupported(size, server.DiskType))
            {
                issues.Add(ValidationIssue.Error($"servers[{position}].diskType",
                    $"VM size '{size.Name}' cannot use '{server.DiskType}' - it does not support " +
                    $"Premium SSD. Azure refuses this at deployment time. Use " +
                    $"'{DiskTypes.StandardSsd}', or pick a size whose name contains 's' for " +
                    "premium storage support, such as Standard_D2s_v5."));
            }

            var skus = catalog.GetImageSkus(location, server.Image.Publisher, server.Image.Offer);

            if (skus.Count > 0
                && !string.IsNullOrWhiteSpace(server.Image.Sku)
                && !skus.Any(s => Same(s, server.Image.Sku)))
            {
                issues.Add(new ValidationIssue(severity, $"servers[{position}].image.sku",
                    $"Image SKU '{server.Image.Sku}' was not found for " +
                    $"{server.Image.Publisher}/{server.Image.Offer} in '{location}'. " +
                    $"Available: {Sample(skus)}."));
            }
        }
    }

    private static void CheckDedicatedHost(
        DeploymentPlan plan,
        ResourceCatalog catalog,
        List<ValidationIssue> issues,
        ValidationSeverity severity,
        string location)
    {
        if (plan.DedicatedHost is not { Enabled: true } host || string.IsNullOrWhiteSpace(host.Sku))
        {
            return;
        }

        var skus = catalog.GetDedicatedHostSkus(location);

        if (skus.Count > 0 && !skus.Any(s => Same(s, host.Sku)))
        {
            issues.Add(new ValidationIssue(severity, "dedicatedHost.sku",
                $"Dedicated host SKU '{host.Sku}' is not offered in '{location}'. " +
                $"Available: {Sample(skus)}."));
        }
    }

    private static void CheckAvd(
        DeploymentPlan plan,
        ResourceCatalog catalog,
        List<ValidationIssue> issues,
        ValidationSeverity severity)
    {
        if (plan.Avd is not { Enabled: true } avd || string.IsNullOrWhiteSpace(avd.MetadataLocation))
        {
            return;
        }

        var avdLocations = catalog.GetAvdLocations().Select(l => l.Name).ToList();

        if (avdLocations.Count > 0 && !avdLocations.Any(l => Same(l, avd.MetadataLocation)))
        {
            issues.Add(new ValidationIssue(severity, "avd.metadataLocation",
                $"'{avd.MetadataLocation}' does not host AVD metadata. " +
                $"Supported: {Sample(avdLocations)}."));
        }
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the message useful without pasting a hundred SKU names into the UI.</summary>
    private static string Sample(IEnumerable<string> values)
    {
        var list = values.ToList();
        var shown = string.Join(", ", list.Take(5));

        return list.Count > 5 ? $"{shown} (+{list.Count - 5} more)" : shown;
    }
}
