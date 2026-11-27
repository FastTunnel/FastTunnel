using System.IO.Pipelines;
using System.Net.WebSockets;

namespace FastTunnel.Core.P2P;

internal sealed class PeerPipe : IDuplexPipe
{
    internal Pipe Inbound { get; } = new();
    internal Pipe Outbound { get; } = new();
    public PipeReader Input => Inbound.Reader;
    public PipeWriter Output => Outbound.Writer;

    internal async Task RunAsync(Func<PipeWriter, CancellationToken, Task> receive,
        Func<PipeReader, CancellationToken, Task> send, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receiving = receive(Inbound.Writer, lifetime.Token);
        var sending = send(Outbound.Reader, lifetime.Token);
        try { await await Task.WhenAny(receiving, sending); }
        finally
        {
            lifetime.Cancel();
            Inbound.Writer.CancelPendingFlush();
            Outbound.Reader.CancelPendingRead();
            await Task.WhenAll(receiving, sending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await Inbound.Writer.CompleteAsync();
            await Outbound.Reader.CompleteAsync();
        }
    }
}

internal sealed class PeerWebSocket(WebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim sendGate = new(1);
    internal PeerPipe Pipe { get; } = new();
    internal bool PolicyClosed => socket.CloseStatus == WebSocketCloseStatus.PolicyViolation;

    internal Task RunAsync(Action<string> signal, CancellationToken token)
        => Pipe.RunAsync((writer, ct) => ReceiveAsync(writer, signal, ct), SendDataAsync, token);

    internal async Task SendSignalAsync(string signal, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try { await PeerProtocol.SendTextAsync(socket, signal, token); }
        finally { sendGate.Release(); }
    }

    private async Task ReceiveAsync(PipeWriter writer, Action<string> signal, CancellationToken token)
    {
        using var text = new MemoryStream();
        var buffer = new byte[PeerProtocol.ChunkSize];
        while (!token.IsCancellationRequested)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (frame.MessageType == WebSocketMessageType.Close)
                return;

            if (frame.MessageType == WebSocketMessageType.Binary)
            {
                var flush = await writer.WriteAsync(buffer.AsMemory(0, frame.Count), token);
                if (flush.IsCanceled || flush.IsCompleted)
                    return;
                continue;
            }

            if (text.Length + frame.Count > PeerProtocol.MaxSignalSize)
                throw new IOException("P2P 信令消息过大");

            text.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage)
            {
                signal(System.Text.Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length));
                text.SetLength(0);
            }
        }
    }

    private async Task SendDataAsync(PipeReader reader, CancellationToken token)
    {
        while (true)
        {
            var read = await reader.ReadAsync(token);
            try
            {
                if (read.IsCanceled)
                    return;

                foreach (var segment in read.Buffer)
                {
                    if (segment.IsEmpty)
                        continue;

                    await sendGate.WaitAsync(token);
                    try { await socket.SendAsync(segment, WebSocketMessageType.Binary, true, token); }
                    finally { sendGate.Release(); }
                }
            }
            finally { reader.AdvanceTo(read.Buffer.End); }

            if (read.IsCompleted)
                return;
        }
    }

    public void Dispose() => sendGate.Dispose();
}
