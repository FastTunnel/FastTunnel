using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace FastTunnel.Core.P2P;

internal static class PeerStun
{
    private const uint Cookie = 0x2112A442;

    private const ushort CoordinatorPadding = 0xC063;
    private const int CoordinatorRequestSize = 44;

    internal static byte[] CreateRequest(bool coordinator = false)
    {
        var request = new byte[coordinator ? CoordinatorRequestSize : 20];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), Cookie);
        RandomNumberGenerator.Fill(request.AsSpan(8, 12));
        if (coordinator)
        {
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 24);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20), CoordinatorPadding);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22), 20);
        }
        return request;
    }

    internal static int WriteCoordinatorResponse(ReadOnlySpan<byte> request, IPEndPoint source, Span<byte> response)
    {
        // Require padding so the reply is never larger than the request, even for IPv6.
        if (request.Length != CoordinatorRequestSize || BinaryPrimitives.ReadUInt16BigEndian(request) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(request[2..]) != 24
            || BinaryPrimitives.ReadUInt32BigEndian(request[4..]) != Cookie
            || BinaryPrimitives.ReadUInt16BigEndian(request[20..]) != CoordinatorPadding
            || BinaryPrimitives.ReadUInt16BigEndian(request[22..]) != 20)
            return 0;

        var address = source.Address.IsIPv4MappedToIPv6 ? source.Address.MapToIPv4() : source.Address;
        Span<byte> raw = stackalloc byte[16];
        if (source.Port == 0 || !address.TryWriteBytes(raw, out var length))
            return 0;

        request[..20].CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(response[2..], (ushort)(8 + length));
        BinaryPrimitives.WriteUInt16BigEndian(response[20..], 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(response[22..], (ushort)(4 + length));
        response[24] = 0;
        response[25] = length == 4 ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(response[26..], (ushort)(source.Port ^ (Cookie >> 16)));
        for (var i = 0; i < length; i++)
            response[28 + i] = (byte)(raw[i] ^ request[4 + i]);
        return 28 + length;
    }

    internal static bool IsResponse(ReadOnlySpan<byte> packet)
        => packet.Length >= 20 && BinaryPrimitives.ReadUInt16BigEndian(packet) == 0x0101
            && BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) == Cookie;

    internal static IPEndPoint? ReadMappedAddress(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> request)
    {
        if (request.Length < 20 || !IsResponse(packet) || !packet[4..20].SequenceEqual(request[4..20])
            || BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != packet.Length - 20)
            return null;

        for (var offset = 20; offset <= packet.Length - 4;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4;
            var padded = (size + 3) & ~3;
            if (padded > packet.Length - offset)
                return null;

            if (type == 0x0020 && size >= 4)
            {
                var addressSize = packet[offset + 1] switch { 1 => 4, 2 => 16, _ => 0 };
                if (addressSize == 0 || size != addressSize + 4)
                    return null;

                var port = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]) ^ (int)(Cookie >> 16);
                Span<byte> address = stackalloc byte[16];
                for (var i = 0; i < addressSize; i++)
                    address[i] = (byte)(packet[offset + 4 + i] ^ request[4 + i]);
                var ip = new IPAddress(address[..addressSize]);
                return port != 0 && PeerUdpHub.IsCandidate(ip) ? new IPEndPoint(ip, port) : null;
            }

            offset += padded;
        }

        return null;
    }
}
