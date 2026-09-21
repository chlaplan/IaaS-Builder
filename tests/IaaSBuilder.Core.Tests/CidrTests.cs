using System.Net;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

public class CidrTests
{
    [Theory]
    [InlineData("10.0.0.0/16")]
    [InlineData("192.168.1.0/24")]
    [InlineData("10.10.0.0/8")]
    public void TryParse_accepts_valid_ranges(string value) =>
        Assert.True(Cidr.TryParse(value, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.256/24")]
    [InlineData("not-a-cidr")]
    [InlineData("::1/64")]
    public void TryParse_rejects_invalid_ranges(string? value) =>
        Assert.False(Cidr.TryParse(value, out _));

    [Fact]
    public void TryParse_normalises_to_the_network_address()
    {
        Assert.True(Cidr.TryParse("10.1.2.3/24", out var cidr));
        Assert.Equal("10.1.2.0/24", cidr.ToString());
    }

    [Fact]
    public void Contains_detects_subnet_containment()
    {
        Assert.True(Cidr.TryParse("10.10.0.0/16", out var vnet));
        Assert.True(Cidr.TryParse("10.10.5.0/24", out var inside));
        Assert.True(Cidr.TryParse("10.20.0.0/24", out var outside));

        Assert.True(vnet.Contains(inside));
        Assert.False(vnet.Contains(outside));

        // A larger range is never contained by a smaller one.
        Assert.False(inside.Contains(vnet));
    }

    [Fact]
    public void Overlaps_is_symmetric()
    {
        Assert.True(Cidr.TryParse("10.10.0.0/24", out var a));
        Assert.True(Cidr.TryParse("10.10.0.128/25", out var b));
        Assert.True(Cidr.TryParse("10.10.1.0/24", out var c));

        Assert.True(a.Overlaps(b));
        Assert.True(b.Overlaps(a));
        Assert.False(a.Overlaps(c));
        Assert.False(c.Overlaps(a));
    }

    [Theory]
    // Azure reserves x.x.x.0 through x.x.x.3 and the broadcast address.
    [InlineData("10.10.0.0", true)]
    [InlineData("10.10.0.1", true)]
    [InlineData("10.10.0.2", true)]
    [InlineData("10.10.0.3", true)]
    [InlineData("10.10.0.4", false)]
    [InlineData("10.10.0.254", false)]
    [InlineData("10.10.0.255", true)]
    public void IsAzureReserved_matches_the_documented_reservations(string address, bool expected)
    {
        Assert.True(Cidr.TryParse("10.10.0.0/24", out var subnet));
        Assert.Equal(expected, subnet.IsAzureReserved(IPAddress.Parse(address)));
    }

    [Fact]
    public void AddressCount_is_correct()
    {
        Assert.True(Cidr.TryParse("10.10.0.0/24", out var slash24));
        Assert.True(Cidr.TryParse("10.10.0.0/16", out var slash16));

        Assert.Equal(256, slash24.AddressCount);
        Assert.Equal(65536, slash16.AddressCount);
    }
}
