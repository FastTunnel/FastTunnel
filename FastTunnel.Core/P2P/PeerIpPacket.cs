using System.Buffers.Binary;
using System.Net;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 虚拟网卡上收到的 IPv4 包。这里只解析到转发决策需要的程度：
/// 版本、首部长度、协议号、源/目的地址和传输层的两个端口，由两端内核自己处理 TCP 状态。
/// 对端送来的包不可信，所以解析时校验首部长度与首部校验和。
/// </summary>
internal readonly struct PeerIpPacket
{
    internal const int MinHeaderSize = 20;
    internal const byte ProtocolTcp = 6;
    internal const byte ProtocolUdp = 17;

    private readonly ReadOnlyMemory<byte> data;

    private PeerIpPacket(ReadOnlyMemory<byte> data, int headerLength, byte protocol,
        IPAddress source, IPAddress destination)
    {
        this.data = data;
        HeaderLength = headerLength;
        Protocol = protocol;
        Source = source;
        Destination = destination;
    }

    internal byte Protocol { get; }

    internal IPAddress Source { get; }

    internal IPAddress Destination { get; }

    /// <summary>IPv4 首部长度（含选项）。</summary>
    internal int HeaderLength { get; }

    /// <summary>整个包的长度，取自总长度字段，可能小于实际收到的字节数（以太网填充）。</summary>
    internal int TotalLength => BinaryPrimitives.ReadUInt16BigEndian(data.Span[2..]);

    /// <summary>是否分片包：只有 0 号分片带传输层端口，后续分片无法据此选路。</summary>
    internal bool IsFragment => (FlagsAndOffset & 0x3FFF) != 0;

    private ushort FlagsAndOffset => BinaryPrimitives.ReadUInt16BigEndian(data.Span[6..]);

    /// <summary>传输层载荷（TCP / UDP 首部 + 数据）。</summary>
    internal ReadOnlyMemory<byte> Payload => data[HeaderLength..TotalLength];

    /// <summary>TCP 或 UDP 的首部都以「源端口、目的端口」开头，这里统一取出来。</summary>
    internal bool TryPorts(out int sourcePort, out int destinationPort)
    {
        sourcePort = destinationPort = 0;
        var payload = Payload.Span;
        if (Protocol is not (ProtocolTcp or ProtocolUdp) || payload.Length < 4)
            return false;

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload[2..4]);
        return true;
    }

    /// <summary>解析并校验一个 IPv4 包。非 IPv4、长度不自洽或首部校验和错误都返回 false。</summary>
    internal static bool TryParse(ReadOnlyMemory<byte> buffer, out PeerIpPacket packet)
    {
        packet = default;
        var span = buffer.Span;
        if (span.Length < MinHeaderSize || span[0] >> 4 != 4)
            return false;

        var header = (span[0] & 0x0F) * 4;
        if (header < MinHeaderSize || header > span.Length)
            return false;

        var total = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if (total < header || total > span.Length)
            return false;

        var version = new ReadOnlySpan<byte>(span[..header].ToArray());
        if (Checksum(version) != 0)
            return false;

        packet = new PeerIpPacket(buffer, header, span[9],
            new IPAddress(span[12..16]), new IPAddress(span[16..20]));
        return true;
    }

    /// <summary>
    /// 一的反码求和。对「已含正确校验和」的首部求和结果为 0，
    /// 因此既能校验收到的包，也能在构造响应前先把校验和字段清零再算。
    /// </summary>
    internal static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0u;
        for (var index = 0; index + 1 < bytes.Length; index += 2)
            sum += (uint)BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index, 2));

        if ((bytes.Length & 1) != 0)
            sum += (uint)bytes[^1] << 8;

        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }
}
