using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FastTunnel.Core.P2P;

internal enum PeerPacketType : byte { Probe, ProbeReply, Select, Selected, Data, Ack, Ping, Pong, Commit, Committed, Ready }

internal sealed class PeerUdpPacket(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey, Guid local, Guid remote) : IDisposable
{
    private const int Header = 24;
    private const int Tag = 16;
    internal const int Overhead = Header + Tag + 9;
    internal const int MaxSize = 1200;
    internal const int MaxPayload = MaxSize - Overhead;
    private readonly AesGcm send = new(sendKey, Tag);
    private readonly AesGcm receive = new(receiveKey, Tag);
    private long nextNonce;
    private ulong highest;
    private ulong recent;

    internal byte[] Write(PeerPacketType type, ulong sequence, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[Overhead + payload.Length];
        remote.TryWriteBytes(packet);
        var number = Interlocked.Increment(ref nextNonce);
        if (number <= 0)
            throw new IOException("P2P packet counter exhausted");

        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(16), number);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        packet.AsSpan(16, 8).CopyTo(nonce[4..]);
        Span<byte> plaintext = stackalloc byte[9 + payload.Length];
        plaintext[0] = (byte)type;
        BinaryPrimitives.WriteUInt64BigEndian(plaintext[1..], sequence);
        payload.CopyTo(plaintext[9..]);
        lock (send)
            send.Encrypt(nonce, plaintext, packet.AsSpan(Header, plaintext.Length),
                packet.AsSpan(Header + plaintext.Length), packet.AsSpan(0, Header));
        return packet;
    }

    internal bool TryRead(ReadOnlySpan<byte> packet, Span<byte> plaintext, out PeerPacketType type, out ulong sequence)
    {
        type = default;
        sequence = 0;
        if (packet.Length is < Overhead or > MaxSize || new Guid(packet[..16]) != local)
            return false;

        var number = BinaryPrimitives.ReadUInt64BigEndian(packet[16..]);
        var behind = highest >= number ? highest - number : 0;
        if (number == 0 || (highest >= number && (behind >= 64 || (recent & (1UL << (int)behind)) != 0)))
            return false;

        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        packet.Slice(16, 8).CopyTo(nonce[4..]);
        try
        {
            receive.Decrypt(nonce, packet[Header..^Tag], packet[^Tag..],
                plaintext[..(packet.Length - Header - Tag)], packet[..Header]);
        }
        catch (AuthenticationTagMismatchException) { return false; }

        if (number > highest)
        {
            var shift = number - highest;
            recent = shift >= 64 ? 1 : (recent << (int)shift) | 1;
            highest = number;
        }
        else
        {
            recent |= 1UL << (int)behind;
        }

        type = (PeerPacketType)plaintext[0];
        sequence = BinaryPrimitives.ReadUInt64BigEndian(plaintext[1..]);
        return true;
    }

    public void Dispose()
    {
        send.Dispose();
        receive.Dispose();
    }
}
