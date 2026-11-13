using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using FastTunnel.Core.Client;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 端口转发路径的连接回收回归测试。
/// 背景：隧道在 10 秒内没建立起来时，ForwardDispatcher 曾把已 accept 的用户 socket 留在没人负责关闭的境地，
/// 服务端的连接和缓冲一直驻留（表现为内存 / ESTABLISHED 只涨不降）。
/// 注意：第一个用例依赖源码里写死的 10 秒隧道超时，跑完要十几秒。
/// </summary>
public class ForwardDispatcherConnectionTests
{
    private const int TunnelTimeoutWaitMs = 25_000;

    private static (ForwardDispatcher Dispatcher, FastTunnelServer Server, PortProxyListener Listener) CreateHarness(
        FakeControlWebSocket control)
    {
        var server = new FastTunnelServer(NullLogger<FastTunnelServer>.Instance, null!, null!);
        var config = new ForwardConfig
        {
            RemotePort = 12345,
            LocalIp = IPAddress.Loopback.ToString(),
            LocalPort = 12346,
            Protocol = ProtocolEnum.TCP,
        };
        var listener = new PortProxyListener(IPAddress.Loopback.ToString(), 0, NullLogger.Instance, control);
        return (new ForwardDispatcher(NullLogger.Instance, server, config), server, listener);
    }

    /// <summary>模拟一次用户接入：返回交给 dispatcher 的那一端，以及留在“用户”手里的那一端。</summary>
    private static async Task<(Socket Accepted, TcpClient User)> AcceptUserPair()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;

        var user = new TcpClient();
        var connecting = user.ConnectAsync(IPAddress.Loopback, port);
        var acceptedTask = probe.AcceptTcpClientAsync();
        await connecting;
        var serverSide = await acceptedTask;
        probe.Stop();
        return (serverSide.Client, user);
    }

    /// <summary>对端 FIN/RST 返回 true；超时仍未被关闭返回 false，也就是这条连接漏了。</summary>
    private static async Task<bool> PeerClosedAsync(TcpClient user, int withinMs)
    {
        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(withinMs);
        try
        {
            while (true)
            {
                int read = await user.GetStream().ReadAsync(buffer, timeout.Token);
                if (read <= 0) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string marker, int withinMs)
    {
        var buffer = new byte[64];
        var text = new StringBuilder();
        using var timeout = new CancellationTokenSource(withinMs);
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                break;
            }
            if (read <= 0) break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    [Fact]
    public async Task DispatchAsync_closes_user_socket_when_tunnel_never_opens()
    {
        var control = new FakeControlWebSocket();
        var (dispatcher, server, listener) = CreateHarness(control);
        var (accepted, user) = await AcceptUserPair();
        using (user)
        {
            var dispatch = dispatcher.DispatchAsync(accepted, control, listener);

            var command = await control.NextCommand(TimeSpan.FromSeconds(5));
            Assert.NotNull(command);
            Assert.Equal(MessageType.Forward, command.Value.Type);

            // 故意不回应这条指令：10 秒后服务端应当超时并关掉用户连接，而不是让它永久挂着
            var closed = await PeerClosedAsync(user, TunnelTimeoutWaitMs);
            Assert.True(closed,
                $"隧道超时 {TunnelTimeoutWaitMs / 1000} 秒后用户连接仍是打开的：ForwardDispatcher 漏掉了已 accept 的 socket");

            await dispatch;
            Assert.Empty(server.ResponseTasks);
        }
    }

    [Fact]
    public async Task DispatchAsync_relays_both_directions_and_closes_pair_when_tunnel_ends()
    {
        var control = new FakeControlWebSocket();
        var (dispatcher, server, listener) = CreateHarness(control);
        var (accepted, user) = await AcceptUserPair();
        using (user)
        {
            var dispatch = dispatcher.DispatchAsync(accepted, control, listener);
            var command = await control.NextCommand(TimeSpan.FromSeconds(5));
            Assert.NotNull(command);
            var msgId = command.Value.Body.Split('|')[0];

            // 假客户端按协议把隧道送进来，做法与 FastTunnelSwapHandler 一致：交给 ResponseTasks 里的 tcs
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            var tunnelPeer = new TcpClient();
            var connecting = tunnelPeer.ConnectAsync(IPAddress.Loopback, port);
            var tunnelSide = await probe.AcceptTcpClientAsync();
            await connecting;
            probe.Stop();

            Assert.True(server.ResponseTasks.TryGetValue(msgId, out var entry));
            Assert.True(entry!.Item1.TrySetResult(new NetworkStream(tunnelSide.Client, true)));

            await tunnelPeer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("HELLO-FROM-USER"));
            Assert.Contains("HELLO-FROM-USER",
                await ReadUntilAsync(user.GetStream(), "HELLO-FROM-USER", 5000));

            await user.GetStream().WriteAsync(Encoding.UTF8.GetBytes("HELLO-FROM-TARGET"));
            Assert.Contains("HELLO-FROM-TARGET",
                await ReadUntilAsync(tunnelPeer.GetStream(), "HELLO-FROM-TARGET", 5000));

            // 隧道结束后用户连接必须跟着关掉：两条流是成对释放的
            tunnelPeer.Close();
            var finished = await Task.WhenAny(dispatch, Task.Delay(10_000));
            Assert.True(finished == dispatch, "隧道关闭后 DispatchAsync 没有结束");
            Assert.True(await PeerClosedAsync(user, 5000), "隧道结束后用户连接没有被关闭");
            Assert.Empty(server.ResponseTasks);
        }
    }

    [Fact]
    public async Task DispatchAsync_closes_user_socket_when_client_already_offline()
    {
        var control = new FakeControlWebSocket { ThrowOnSend = new SocketClosedException("Aborted") };
        var (dispatcher, server, listener) = CreateHarness(control);
        var (accepted, user) = await AcceptUserPair();
        using (user)
        {
            var dispatch = dispatcher.DispatchAsync(accepted, control, listener);

            Assert.True(await PeerClosedAsync(user, 5000), "客户端已离线时用户连接没有被关闭");
            await dispatch;
            Assert.Empty(server.ResponseTasks);
        }
    }

    /// <summary>只记账不断线的控制通道替身：SendAsync 记录指令，ReceiveAsync 永不返回。</summary>
    private sealed class FakeControlWebSocket : WebSocket
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly Queue<(MessageType Type, string Body)> _commands = new();

        public Exception? ThrowOnSend { get; init; }

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override void Abort() { }

        public override void Dispose() { }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (ThrowOnSend != null) return Task.FromException(ThrowOnSend);

            var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _commands.Enqueue(((MessageType)line[0], line[1..]));
                _arrived.Release();
            }
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => new TaskCompletionSource<WebSocketReceiveResult>().Task;

        public async Task<(MessageType Type, string Body)?> NextCommand(TimeSpan within)
        {
            if (!await _arrived.WaitAsync(within)) return null;
            return _commands.Dequeue();
        }
    }
}
