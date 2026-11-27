using System.Buffers.Binary;
using System.Net;
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
