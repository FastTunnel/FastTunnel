using System.Buffers.Binary;
using System.Net;
using System.Text;
using FastTunnel.Core.P2P;

namespace FastTunnel.Core.Tests.Support;

/// <summary>
/// 构造测试用 IPv4 包：只填组网转发会读到的字段，并写好首部校验和，
/// 让 <see cref="PeerIpPacket"/> 愿意信任它。改写字段后用 <see cref="Sign"/> 重新签名。
/// </summary>
public static class TestPackets
{
    public static byte[] Ipv4(byte protocol, string source, string destination, byte[] payload)
    {
        var packet = new byte[20 + payload.Length];
        packet[0] = 0x45;
        packet[2] = (byte)(packet.Length >> 8);
        packet[3] = (byte)packet.Length;
        packet[8] = 64;
        packet[9] = protocol;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        payload.CopyTo(packet, 20);
        return Sign(packet);
    }

    /// <summary>清零校验和字段后重新计算，写回大端两字节。</summary>
    public static byte[] Sign(byte[] packet)
    {
        packet[10] = 0;
        packet[11] = 0;
        var checksum = PeerIpPacket.Checksum(packet.AsSpan(0, (packet[0] & 0x0F) * 4));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), checksum);
        return packet;
    }
}

/// <summary>造单问题 DNS 报文：组网解析器会真解析这些字节，所以按线上格式写。</summary>
public static class TestDns
{
    /// <summary>单问题 DNS 报文；<paramref name="response"/> 为真时带上响应位，用来喂给应答器当垃圾输入。</summary>
    public static byte[] Query(string name, ushort qType = 1, ushort qClass = 1, ushort id = 0x4F2A, bool response = false)
    {
        var labels = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            labels.Add((byte)bytes.Length);
            labels.AddRange(bytes);
        }

        labels.Add(0);
        var query = new byte[12 + labels.Count + 4];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2, 2), (ushort)(response ? 0x8180 : 0x0100));
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
        labels.CopyTo(query.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(12 + labels.Count, 2), qType);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(12 + labels.Count + 2, 2), qClass);
        return query;
    }
}
