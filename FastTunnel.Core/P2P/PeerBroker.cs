using System.Buffers;
using System.Net.WebSockets;
using FastTunnel.Core.Security;
using Microsoft.AspNetCore.Http;

namespace FastTunnel.Core.P2P;

internal sealed class PeerBroker
{
    private readonly object gate = new();
    private readonly Dictionary<(string Owner, string Service), List<Slot>> waiting = new();
    private readonly Dictionary<string, int> open = new(StringComparer.OrdinalIgnoreCase);
    private int total;

    internal async Task RunAsync(HttpContext context, ClientCredential credential, Func<ClientCredential, bool> authorized)
    {
        var service = context.Request.Headers[PeerProtocol.Service].ToString();
        var provider = context.Request.Headers[PeerProtocol.Provider].ToString();
        var visitors = context.Request.Headers[PeerProtocol.Visitors].ToString();
        var direct = context.Request.Headers[PeerProtocol.Direct].ToString();
        if (!PeerProtocol.IsName(service) || !PeerProtocol.IsName(credential.Id)
            || (provider.Length == 0 ? visitors.Length > 4096 : !PeerProtocol.IsName(provider) || visitors.Length != 0))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var owner = credential.Id.ToUpperInvariant();
        lock (gate)
        {
            var count = open.GetValueOrDefault(owner);
            if (total >= 4096 || count >= 128)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            open[owner] = count + 1;
            total++;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            if (provider.Length == 0)
            {
                var allowed = visitors.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (allowed.Length == 0 || !allowed.All(PeerProtocol.IsName))
                    return;

                await PublishAsync((owner, service), new Slot(socket, credential, allowed, direct), context.RequestAborted);
            }
            else
            {
                await VisitAsync((provider.ToUpperInvariant(), service), socket, credential, direct,
                    authorized, context.RequestAborted);
            }
        }
        catch (WebSocketException)
        {
            // Either peer may disappear while pairing or forwarding.
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                if (--open[owner] == 0)
                    open.Remove(owner);
                total--;
            }
        }
    }

    private async Task PublishAsync((string, string) key, Slot slot, CancellationToken token)
    {
        // The provider sends nothing until it is paired. Start reading now so a dead
        // socket is removed immediately. The visitor handler takes over this read.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var first = new byte[PeerProtocol.ChunkSize];
        slot.FirstBuffer = first;
        slot.FirstRead = slot.Socket.ReceiveAsync(first.AsMemory(), lifetime.Token).AsTask();
        lock (gate)
        {
            if (!waiting.TryGetValue(key, out var slots))
                waiting.Add(key, slots = []);
            slots.Add(slot);
        }
        try
        {
            var idle = Task.Delay(TimeSpan.FromMinutes(3), lifetime.Token);
            var next = await Task.WhenAny(slot.Claimed.Task, slot.FirstRead, idle);
            lock (gate)
            {
                if (waiting.TryGetValue(key, out var slots))
                {
                    slots.Remove(slot);
                    if (slots.Count == 0)
                        waiting.Remove(key);
                }
            }
            if (slot.Claimed.Task.IsCompleted)
                await slot.Finished.Task;
            else if (next == idle && !token.IsCancellationRequested && slot.Socket.State == WebSocketState.Open)
                await slot.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token);
        }
        finally
        {
            lifetime.Cancel();
            try { await slot.FirstRead; }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
        }
    }

    private async Task VisitAsync((string, string) key, WebSocket visitor, ClientCredential credential,
        string direct, Func<ClientCredential, bool> authorized, CancellationToken token)
    {
        Slot? slot;
        lock (gate)
        {
            slot = waiting.GetValueOrDefault(key)?.FirstOrDefault(candidate =>
                !candidate.Claimed.Task.IsCompleted
                && candidate.FirstRead?.IsCompleted != true
                && candidate.Allowed.Contains(credential.Id, StringComparer.OrdinalIgnoreCase)
                && authorized(candidate.Credential));
            slot?.Claimed.TrySetResult();
        }

        if (slot is null)
        {
            await visitor.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Service unavailable", token);
            return;
        }

        try
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
            var paired = direct is PeerProtocol.DirectVersion or "3" or "2" && direct == slot.Direct ? "direct" : "relay";
            await Task.WhenAll(PeerProtocol.SendTextAsync(slot.Socket, paired, session.Token),
                PeerProtocol.SendTextAsync(visitor, paired, session.Token));

            using var providerWrite = new SemaphoreSlim(1);
            using var visitorWrite = new SemaphoreSlim(1);
            var uplink = CopyAsync(visitor, slot.Socket, providerWrite, session.Token);
            var downlink = CopyAsync(slot.Socket, visitor, visitorWrite, session.Token, slot);
            var authorization = MonitorAsync(slot.Credential, credential, authorized, session.Token);
            try
            {
                var finished = await Task.WhenAny(uplink, downlink, authorization);
                await finished;
                if (finished == authorization)
                {
                    using var revoke = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    revoke.CancelAfter(TimeSpan.FromSeconds(1));
                    await Task.WhenAll(CloseRevokedAsync(visitor, visitorWrite, revoke.Token),
                        CloseRevokedAsync(slot.Socket, providerWrite, revoke.Token))
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            finally
            {
                session.Cancel();
                await Task.WhenAll(uplink, downlink, authorization).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally { slot.Finished.TrySetResult(); }
    }

    private static async Task CloseRevokedAsync(WebSocket socket, SemaphoreSlim sendGate, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Authorization revoked", token); }
        finally { sendGate.Release(); }
    }

    private static async Task MonitorAsync(ClientCredential provider, ClientCredential visitor,
        Func<ClientCredential, bool> authorized, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(token))
            if (!authorized(provider) || !authorized(visitor))
                return;
    }

    private static async Task CopyAsync(WebSocket from, WebSocket to, SemaphoreSlim sendGate,
        CancellationToken token, Slot? provider = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PeerProtocol.ChunkSize);
        try
        {
            if (provider != null && !await Forward(await provider.FirstRead!.WaitAsync(token), provider.FirstBuffer!))
                return;

            while (await Forward(await from.ReceiveAsync(buffer.AsMemory(0, PeerProtocol.ChunkSize), token), buffer))
            {
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        async ValueTask<bool> Forward(ValueWebSocketReceiveResult frame, byte[] bytes)
        {
            if (frame.MessageType == WebSocketMessageType.Close)
                return false;
            await sendGate.WaitAsync(token);
            try { await to.SendAsync(bytes.AsMemory(0, frame.Count), frame.MessageType, frame.EndOfMessage, token); }
            finally { sendGate.Release(); }
            return true;
        }
    }

    private sealed class Slot(WebSocket socket, ClientCredential credential, string[] allowed, string direct)
    {
        internal WebSocket Socket { get; } = socket;
        internal ClientCredential Credential { get; } = credential;
        internal string[] Allowed { get; } = allowed;
        internal string Direct { get; } = direct;
        internal TaskCompletionSource Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? FirstBuffer { get; set; }
        internal Task<ValueWebSocketReceiveResult>? FirstRead { get; set; }
    }
}
