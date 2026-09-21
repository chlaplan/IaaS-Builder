using System.Net;
using System.Net.Sockets;

namespace IaaSBuilder.Core.Validation;

/// <summary>An IPv4 CIDR range, with the containment checks the legacy tool never had.</summary>
public readonly struct Cidr
{
    private Cidr(uint network, int prefixLength)
    {
        Network = network;
        PrefixLength = prefixLength;
    }

    public uint Network { get; }
    public int PrefixLength { get; }

    public uint Mask => PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
    public uint Broadcast => Network | ~Mask;

    /// <summary>Total addresses in the range, including network and broadcast.</summary>
    public long AddressCount => 1L << (32 - PrefixLength);

    public static bool TryParse(string? value, out Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split('/');
        if (parts.Length != 2) return false;

        if (!IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength) || prefixLength is < 0 or > 32)
        {
            return false;
        }

        var raw = ToUInt32(address);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        cidr = new Cidr(raw & mask, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        (ToUInt32(address) & Mask) == Network;

    /// <summary>
    /// Builds a range from a raw address and prefix length, rejecting one whose address is not on
    /// a boundary for that length. <c>10.0.0.64/25</c> is not a range, it is a typo, and silently
    /// rounding it down would hand back something the caller never asked for.
    /// </summary>
    public static bool TryCreate(uint network, int prefixLength, out Cidr cidr)
    {
        cidr = default;
        if (prefixLength is < 0 or > 32) return false;

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        if ((network & mask) != network) return false;

        cidr = new Cidr(network, prefixLength);
        return true;
    }

    /// <summary>True when this range wholly contains <paramref name="other"/>.</summary>
    public bool Contains(Cidr other) =>
        other.PrefixLength >= PrefixLength && (other.Network & Mask) == Network;

    public bool Overlaps(Cidr other) =>
        Network <= other.Broadcast && other.Network <= Broadcast;

    /// <summary>
    /// Azure reserves the first four and the last address of every subnet.
    /// Assigning one of them is a classic silent deployment failure.
    /// </summary>
    public bool IsAzureReserved(IPAddress address)
    {
        if (!Contains(address)) return false;
        var raw = ToUInt32(address);
        return raw <= Network + 3 || raw == Broadcast;
    }

    public override string ToString() => $"{ToIpAddress(Network)}/{PrefixLength}";

    /// <summary>
    /// The first address Azure will actually let you assign, or null when the subnet is too
    /// small to hold one.
    /// </summary>
    public string? FirstUsableAddress() =>
        Network + 4 < Broadcast ? ToIpAddress(Network + 4).ToString() : null;

    /// <summary>
    /// The last address Azure will actually let you assign, or null when the subnet is too
    /// small to hold one. The broadcast address is reserved.
    /// </summary>
    public string? LastUsableAddress() =>
        Network + 4 < Broadcast ? ToIpAddress(Broadcast - 1).ToString() : null;

    /// <summary>Addresses left after Azure's five reserved ones, floored at zero.</summary>
    public long UsableAddressCount() => Math.Max(0, AddressCount - 5);

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress ToIpAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
