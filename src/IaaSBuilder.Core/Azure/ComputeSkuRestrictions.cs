using System.Text.Json;

namespace IaaSBuilder.Core.Azure;

/// <summary>
/// Reads VM size restrictions out of a <c>Microsoft.Compute/skus</c> response.
/// </summary>
/// <remarks>
/// <para>
/// Azure's SKU list answers "does this size exist in this region", not "may this subscription
/// deploy it". The restriction array on the same response answers the second question, and
/// ignoring it is why a lab could be planned around a size that Azure then refuses with
/// <c>SkuNotAvailable</c> during template validation - two seconds into a deployment that has
/// already created a resource group and a virtual network. For one real subscription in
/// usgovvirginia the region reports 952 sizes of which 73 are refused.
/// </para>
/// <para>
/// Separated from the service so it can be tested against a captured response. The parsing is the
/// part with the edge cases; the HTTP call is not.
/// </para>
/// </remarks>
public static class ComputeSkuRestrictions
{
    /// <summary>
    /// The VM size names the subscription may not deploy in <paramref name="location"/>.
    /// </summary>
    /// <param name="skus">The <c>value</c> array from the SKUs response.</param>
    /// <remarks>
    /// Only <c>Location</c> restrictions count. A <c>Zone</c> restriction means the size is absent
    /// from some availability zones, which says nothing about a deployment that does not pin one -
    /// and this tool does not pin one. Treating zone restrictions as unavailability would hide a
    /// large number of sizes that deploy perfectly well.
    /// </remarks>
    public static IReadOnlySet<string> Read(JsonElement skus, string location)
    {
        var restricted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (skus.ValueKind != JsonValueKind.Array || string.IsNullOrWhiteSpace(location))
        {
            return restricted;
        }

        foreach (var sku in skus.EnumerateArray())
        {
            if (sku.ValueKind != JsonValueKind.Object ||
                !sku.TryGetProperty("resourceType", out var type) ||
                !string.Equals(type.GetString(), "virtualMachines", StringComparison.OrdinalIgnoreCase) ||
                !sku.TryGetProperty("name", out var name) ||
                name.GetString() is not { Length: > 0 } sizeName ||
                !sku.TryGetProperty("restrictions", out var restrictions) ||
                restrictions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var restriction in restrictions.EnumerateArray())
            {
                if (restriction.ValueKind != JsonValueKind.Object ||
                    !restriction.TryGetProperty("type", out var kind) ||
                    !string.Equals(kind.GetString(), "Location", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // "values" is the older shape and "restrictionInfo.locations" the newer one. Both
                // are populated in practice; either naming the region is a refusal.
                var namesRegion =
                    NamesLocation(restriction, "values", location) ||
                    (restriction.TryGetProperty("restrictionInfo", out var info) &&
                     NamesLocation(info, "locations", location));

                if (namesRegion)
                {
                    restricted.Add(sizeName);
                    break;
                }
            }
        }

        return restricted;
    }

    private static bool NamesLocation(JsonElement element, string property, string location) =>
        element.TryGetProperty(property, out var array) &&
        array.ValueKind == JsonValueKind.Array &&
        array.EnumerateArray().Any(
            v => string.Equals(v.GetString(), location, StringComparison.OrdinalIgnoreCase));
}
