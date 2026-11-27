using System.Net;
using System.Net.Sockets;

namespace FastTunnel.Core.P2P;

/// <summary>
/// L3 虚拟网段。只接受 IPv4 私有段（含 100.64.0.0/10），地址按数值从小到大发放，
/// 网络地址与广播地址不参与分配，因此同一份台账重复分配的结果稳定可预期。
/// </summary>
internal sealed class PeerNetworkCidr
{
    private readonly uint first;
    private readonly uint last;

    private PeerNetworkCidr(uint network, uint mask, int prefix, string text)
    {
        first = network + 1;
        last = (network | ~mask) - 1;
        PrefixLength = prefix;
        Text = text;
    }

    /// <summary>配置的原文写法，例如 <c>100.64.0.0/16</c>。</summary>
    internal string Text { get; }

    internal int PrefixLength { get; }

    /// <summary>可发放的地址数。</summary>
    internal int Capacity => (int)(last - first + 1);

    internal uint FirstAddress => first;

    internal uint LastAddress => last;

    internal static IPAddress ToAddress(uint value) => FromUInt32(value);

    /// <summary>解析 CIDR。写法非法、前缀过窄或落在公网段时返回 null。</summary>
    internal static PeerNetworkCidr? Parse(string? cidr)
    {
        var parts = cidr?.Split('/', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 8 or > 30 || !IsPrivate(address))
        {
            return null;
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ToUInt32(address) & mask;
        return new PeerNetworkCidr(network, mask, prefix, $"{FromUInt32(network)}/{prefix}");
    }

    internal bool Contains(IPAddress? address)
        => address is { AddressFamily: AddressFamily.InterNetwork } && Contains(ToUInt32(address));

    internal bool Contains(uint address) => address >= first && address <= last;

    /// <summary>台账里已写的地址字符串 → 数值；写法非法返回 false。</summary>
    internal bool TryAddress(string? text, out uint value)
    {
        value = 0;
        if (!System.Net.IPAddress.TryParse(text ?? "", out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return TryAddress(address, out value);
    }

    internal bool TryAddress(IPAddress? address, out uint value)
    {
        value = address is { AddressFamily: AddressFamily.InterNetwork } parsed ? ToUInt32(parsed) : 0;
        return value != 0 && Contains(value);
    }

    /// <summary>按前缀长度换算子网掩码，创建虚拟网卡时要写进接口配置。</summary>
    internal IPAddress NetMask() => FromUInt32(PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength));

    internal static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    internal static IPAddress FromUInt32(uint value) => new(new byte[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });

    /// <summary>只允许自己的内网段和运营商级 NAT 段，避免误配成公网段后把正常流量吸进隧道。</summary>
    private static bool IsPrivate(IPAddress address)
    {
        var value = ToUInt32(address);
        return (value & 0xFF000000) == 0x0A000000      // 10.0.0.0/8
            || (value & 0xFFF00000) == 0xAC100000      // 172.16.0.0/12
            || (value & 0xFFFF0000) == 0xC0A80000      // 192.168.0.0/16
            || (value & 0xFFC00000) == 0x64400000;     // 100.64.0.0/10（运营商级 NAT）
    }
}
