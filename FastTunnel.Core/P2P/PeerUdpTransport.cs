using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FastTunnel.Core.P2P;

// A bounded, cumulative-ACK byte stream for the existing MUX protocol.
// Congestion limits grow after acknowledged batches and fall after loss.
internal sealed class PeerUdpTransport(PeerUdpHub hub, IPEndPoint endpoint, PeerUdpPacket codec,
    ChannelReader<UdpReceiveResult> incoming, bool visitor)
{
    private const int MaxWindow = 32;
    private readonly Channel<ulong> acknowledgements = Channel.CreateBounded<ulong>(
        new BoundedChannelOptions(MaxWindow) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    internal PeerPipe Pipe { get; } = new();

    internal async Task RunAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var io = Pipe.RunAsync((writer, ct) => ReceiveAsync(writer, lifetime, ct), SendAsync, lifetime.Token);
        var ping = KeepAliveAsync(lifetime.Token);
        try { await await Task.WhenAny(io, ping); }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(io, ping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ReceiveAsync(PipeWriter writer, CancellationTokenSource lifetime, CancellationToken token)
    {
        var plaintext = new byte[PeerUdpPacket.MaxSize];
        var reordered = new Dictionary<ulong, byte[]>();
        ulong expected = 0;
        await foreach (var packet in incoming.ReadAllAsync(token))
        {
            if (!packet.RemoteEndPoint.Equals(endpoint)
                || !codec.TryRead(packet.Buffer, plaintext, out var type, out var number))
                continue;
            lifetime.CancelAfter(TimeSpan.FromSeconds(15));

            var size = packet.Buffer.Length - PeerUdpPacket.Overhead;
            switch (type)
            {
                case PeerPacketType.Data when size > 0:
                    if (number == expected)
                    {
                        writer.Write(plaintext.AsSpan(9, size));
                        expected++;
                        while (reordered.Remove(expected, out var data))
                        {
                            writer.Write(data);
                            expected++;
                        }
                        var flush = await writer.FlushAsync(token);
                        if (flush.IsCompleted || flush.IsCanceled)
                            return;
                    }
                    else if (number > expected && number - expected < MaxWindow && !reordered.ContainsKey(number))
                        reordered.Add(number, plaintext.AsSpan(9, size).ToArray());
                    await SendPacketAsync(PeerPacketType.Ack, expected, ReadOnlyMemory<byte>.Empty, token);
                    break;
                case PeerPacketType.Ack when size == 0:
                    acknowledgements.Writer.TryWrite(number);
                    break;
                case PeerPacketType.Ping when size == 0:
                    await SendPacketAsync(PeerPacketType.Pong, 0, ReadOnlyMemory<byte>.Empty, token);
                    break;
            }
        }
    }

    private async Task SendAsync(PipeReader reader, CancellationToken token)
    {
        ulong sequence = 0;
        var window = 4;
        var chunk = new byte[PeerUdpPacket.MaxPayload];

        while (true)
        {
            var read = await reader.ReadAsync(token);
            var buffer = read.Buffer;
            var consumed = buffer.Start;
            try
            {
                if (read.IsCanceled)
                    return;

                if (!buffer.IsEmpty)
                {
                    var bytes = (int)Math.Min(buffer.Length, window * chunk.Length);
                    var count = (bytes + chunk.Length - 1) / chunk.Length;
                    var batch = buffer.Slice(0, bytes);
                    var acked = 0;
                    var retries = 0;
                    while (acked < count)
                    {
                        var deadline = TimeSpan.FromMilliseconds(Math.Min(240 * (1 << retries), 2400));
                        for (var i = acked; i < count; i++)
                        {
                            var offset = i * chunk.Length;
                            var size = Math.Min(chunk.Length, bytes - offset);
                            batch.Slice(offset, size).CopyTo(chunk);
                            await SendPacketAsync(PeerPacketType.Data, sequence + (ulong)i, chunk.AsMemory(0, size), token);
                            if (i % 8 == 7 && i < count - 1)
                                await Task.Delay(1, token);
                        }

                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(deadline);
                        try
                        {
                            while (acked < count && await acknowledgements.Reader.WaitToReadAsync(wait.Token))
                                while (acknowledgements.Reader.TryRead(out var ack))
                                    if (ack > sequence + (ulong)acked && ack <= sequence + (ulong)count)
                                        acked = (int)(ack - sequence);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            if (retries++ >= 6)
                                throw new IOException("P2P UDP acknowledgement timed out");
                        }
                    }

                    window = retries == 0 ? Math.Min(window + 1, MaxWindow) : Math.Max(window / 2, 1);
                    sequence += (ulong)count;
                    consumed = batch.End;
                }
            }
            finally { reader.AdvanceTo(consumed, consumed); }

            if (read.IsCompleted && consumed.Equals(buffer.End))
                return;
        }
    }

    private async Task KeepAliveAsync(CancellationToken token)
    {
        if (visitor)
            await SendPacketAsync(PeerPacketType.Ping, 0, ReadOnlyMemory<byte>.Empty, token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(token))
            await SendPacketAsync(PeerPacketType.Ping, 0, ReadOnlyMemory<byte>.Empty, token);
    }

    private Task SendPacketAsync(PeerPacketType type, ulong number, ReadOnlyMemory<byte> payload, CancellationToken token)
        => hub.SendAsync(codec.Write(type, number, payload.Span), endpoint, token);
}
