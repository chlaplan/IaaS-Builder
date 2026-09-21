using System.Text.Json;
using IaaSBuilder.Core.Azure;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Reading VM size restrictions out of a <c>Microsoft.Compute/skus</c> response.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a deployment failed with <c>SkuNotAvailable</c> on a size the tool itself
/// had offered in its dropdown. The restriction data had been arriving on every catalog refresh
/// all along, in the same response as the size list, and was simply never read.
/// </para>
/// <para>
/// The JSON below is the real shape, copied verbatim from a live Azure US Government response for
/// usgovvirginia: the region reports 952 virtual machine SKUs, of which 73 carry a
/// <c>Location</c> restriction with <c>reasonCode: NotAvailableForSubscription</c>. Both
/// <c>values</c> and <c>restrictionInfo.locations</c> are populated, which is why the reader
/// accepts either.
/// </para>
/// </remarks>
public class ComputeSkuRestrictionsTests
{
    private static IReadOnlySet<string> Read(string json, string location = "usgovvirginia")
    {
        using var document = JsonDocument.Parse(json);
        return ComputeSkuRestrictions.Read(document.RootElement, location);
    }

    [Fact]
    public void A_location_restriction_naming_the_region_marks_the_size_unavailable()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D1",
            "restrictions": [
              {
                "reasonCode": "NotAvailableForSubscription",
                "restrictionInfo": { "locations": ["usgovvirginia"] },
                "type": "Location",
                "values": ["usgovvirginia"]
              }
            ]
          }
        ]
        """);

        Assert.Equal(["Standard_D1"], restricted);
    }

    [Fact]
    public void A_size_with_no_restrictions_is_available()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D2s_v5",
            "restrictions": []
          }
        ]
        """);

        Assert.Empty(restricted);
    }

    /// <summary>
    /// The distinction that stops this check from blocking most of the catalogue.
    /// </summary>
    /// <remarks>
    /// A Zone restriction means the size is missing from some availability zones. This tool does
    /// not pin a zone, so such a size deploys perfectly well. Counting zone restrictions as
    /// unavailability is the obvious wrong reading of this API, and it would have turned a fix
    /// into a much worse bug than the one it replaced.
    /// </remarks>
    [Fact]
    public void A_zone_restriction_does_not_make_a_size_unavailable()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D4s_v5",
            "restrictions": [
              {
                "reasonCode": "NotAvailableForSubscription",
                "restrictionInfo": { "locations": ["usgovvirginia"], "zones": ["1", "2"] },
                "type": "Zone",
                "values": ["usgovvirginia"]
              }
            ]
          }
        ]
        """);

        Assert.Empty(restricted);
    }

    [Fact]
    public void A_restriction_in_another_region_is_ignored()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D8s_v5",
            "restrictions": [
              {
                "reasonCode": "NotAvailableForSubscription",
                "restrictionInfo": { "locations": ["usgovtexas"] },
                "type": "Location",
                "values": ["usgovtexas"]
              }
            ]
          }
        ]
        """);

        Assert.Empty(restricted);
    }

    /// <summary>
    /// The same response carries disks, host groups and more. Only virtual machine SKUs are sizes.
    /// </summary>
    [Fact]
    public void Non_virtual_machine_skus_are_ignored()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "disks",
            "name": "Premium_LRS",
            "restrictions": [
              {
                "type": "Location",
                "restrictionInfo": { "locations": ["usgovvirginia"] },
                "values": ["usgovvirginia"]
              }
            ]
          }
        ]
        """);

        Assert.Empty(restricted);
    }

    /// <summary>
    /// Older responses carry <c>values</c> without <c>restrictionInfo</c>. Reading only the newer
    /// shape would silently report nothing restricted, which is the failure mode that looks
    /// exactly like success.
    /// </summary>
    [Fact]
    public void The_older_values_shape_is_still_understood()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D11",
            "restrictions": [
              { "type": "Location", "values": ["usgovvirginia"] }
            ]
          }
        ]
        """);

        Assert.Equal(["Standard_D11"], restricted);
    }

    [Fact]
    public void Region_matching_is_case_insensitive()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D12",
            "restrictions": [
              { "type": "Location", "values": ["USGovVirginia"] }
            ]
          }
        ]
        """, "usgovvirginia");

        Assert.Equal(["Standard_D12"], restricted);
    }

    /// <summary>
    /// Malformed or unexpected input must read as "nothing is restricted", never throw. This runs
    /// inside preflight, where an exception would block a deployment that would have worked.
    /// </summary>
    [Fact]
    public void Junk_reads_as_nothing_restricted_rather_than_throwing()
    {
        Assert.Empty(Read("[]"));
        Assert.Empty(Read("""[ { "name": "Standard_D1" } ]"""));
        Assert.Empty(Read("""[ null, 3, "text" ]"""));
        Assert.Empty(Read("""{ "notAnArray": true }"""));
        Assert.Empty(Read("""[ { "resourceType": "virtualMachines", "name": "X", "restrictions": {} } ]"""));
    }

    [Fact]
    public void A_blank_location_matches_nothing()
    {
        var restricted = Read("""
        [
          {
            "resourceType": "virtualMachines",
            "name": "Standard_D1",
            "restrictions": [
              { "type": "Location", "values": ["usgovvirginia"] }
            ]
          }
        ]
        """, "");

        Assert.Empty(restricted);
    }
}
