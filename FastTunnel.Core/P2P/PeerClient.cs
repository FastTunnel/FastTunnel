using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FastTunnel.Core.Config;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Sockets;
using FastTunnel.Core.Tls;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.P2P;

internal sealed class PeerClient(IOptionsMonitor<DefaultClientConfig> options, ILogger<PeerClient> logger)
    : BackgroundService
{
    internal static readonly SwapRequest PrivateStream = new() { Id = PeerProtocol.PrivateId, Host = "private", Port = 1 };
    private static readonly TimeSpan DirectDisconnectGrace = TimeSpan.FromSeconds(5);

    /// <summary>目录领取失败的退避上限（秒）、网卡建不起来或停止收发后的重试间隔、服务端明确拒绝组网时的轮询间隔。</summary>
    private const double MaxRetrySeconds = 300;
    private static readonly TimeSpan DeviceRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlockedRetry = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim providerSessions = new(96);
    private PeerUdpHub? udp;
    private PeerTcpHub? tcp;
    private IPEndPoint? tcpEndpoint;
    private SuiDaoServer[] discoveryNodes = [];
    private PeerGatewayMapper? gateway;

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var config = options.CurrentValue;
        var peer = config.P2P;
        if (peer is not { HasEndpoints: true })
            return;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (PeerInvites.IsInvite(config.Token))
        {
            var remaining = PeerInvites.ReadExpiry(config.Token) - DateTimeOffset.UtcNow;
            if (remaining is not { } duration || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(PeerInvites.MaxMinutes + 1))
            {
                logger.LogWarning("P2P 测试邀请无效或已过期");
                return;
            }
            lifetime.CancelAfter(duration);
        }

        var services = peer.GetServices();
        var visitors = peer.GetVisitors();
        var servers = new[] { config.Server }.Concat(peer.AdditionalRelays).ToArray();
        var exits = peer.Socks5 != 0 ? PeerSocks5.ResolveExits(peer, visitors) : [];
        Validate(config, servers, services, visitors, exits);
        discoveryNodes = servers;
        if (peer.EnableDirect)
        {
            if (peer.EnableUdpDirect)
            {
                try { udp = new PeerUdpHub(peer.DirectUdpPort); }
                catch (SocketException ex) { logger.LogWarning("P2P UDP 端口无法绑定 Socket错误={SocketError}", ex.SocketErrorCode); }
            }
            tcp = new PeerTcpHub(peer.DirectTcpPort, logger);
            if (peer.DirectTcpEndpoint != null)
                tcpEndpoint = IPEndPoint.Parse(peer.DirectTcpEndpoint);
        }
        await using var udpLifetime = udp;
        await using var tcpLifetime = tcp;
        var direct = udp != null || tcp != null;
        if (udp != null && peer.AutoMapPort)
            gateway = new PeerGatewayMapper(udp.Port, logger, token);
        await using var gatewayLifetime = gateway;

        // 每个「提供端/服务」只维护一条通道：连接器建一次，本机监听端口和 SOCKS5 出口在同一条路上开流
        var channels = new Dictionary<string, PeerChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in visitors.Select(visitor => (visitor.Provider, visitor.Service))
                     .Concat(exits.Select(exit => (exit.Provider, exit.Service))))
            channels[PeerChannel.Key(target.Provider, target.Service)] =
                new PeerChannel(target.Provider, target.Service, servers.Length);

        var tasks = servers.SelectMany((server, index) => services.Select(service =>
                RunProviderAsync(config, server, service, direct, index, lifetime.Token)))
            .Concat(channels.Values.SelectMany(channel => servers.Select((server, index) =>
                MaintainVisitorAsync(config, server, channel, direct, index, lifetime.Token))))
            .Concat(visitors.Select(visitor => RunVisitorAsync(config,
                channels[PeerChannel.Key(visitor.Provider, visitor.Service)], visitor, lifetime.Token)))
            .Concat(peer.Socks5 != 0 ? new[] { RunSocks5Async(config, exits, channels, lifetime.Token) } : [])
            .Concat(peer.EnableL3 ? new[] { RunL3Async(config, servers, direct, lifetime.Token) } : [])
            .ToArray();
        try { await await Task.WhenAny(tasks); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (!token.IsCancellationRequested)
                logger.LogInformation("P2P 测试邀请已过期，已停止测试隧道");
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static void Validate(DefaultClientConfig config, SuiDaoServer[] servers,
        PeerServiceOptions[] services, PeerVisitorOptions[] visitors, PeerSocks5.Exit[] exits)
    {
        var peer = config.P2P;
        if (servers.Length > 4 || servers.Any(server => server == null
            || !string.Equals(server.Protocol, "wss", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(server.ServerAddr) || !PeerProtocol.IsPort(server.ServerPort)
            || server.P2PDiscoveryPort is < 0 or > 65535)
            || string.IsNullOrWhiteSpace(config.Token)
            || servers.Select(server => (server.ServerAddr.ToUpperInvariant(), server.ServerPort)).Distinct().Count() != servers.Length)
            throw new InvalidOperationException("P2P 要求 WSS 服务端和独立客户端 Token");

        if (services.Length * servers.Length > 64
            || services.Select(service => service.Name).Distinct(StringComparer.Ordinal).Count() != services.Length
            || services.Any(service => !PeerProtocol.IsName(service.Name) || !HasPorts(service)
                || string.IsNullOrWhiteSpace(service.LocalHost) || service.AllowVisitors.Length is 0 or > 60
                || !service.AllowVisitors.All(PeerProtocol.IsName)))
            throw new InvalidOperationException("P2P Services/Share 的服务名称、端口或访客名单无效");

        if (visitors.Any(visitor => !PeerProtocol.IsName(visitor.Provider) || !PeerProtocol.IsName(visitor.Service)
            || !PeerProtocol.IsPort(visitor.ListenPort) || visitor.RemotePort != 0 && !PeerProtocol.IsPort(visitor.RemotePort)
            || !IPAddress.TryParse(visitor.ListenAddress, out var address)
            || !IPAddress.IsLoopback(NetUtil.Unmap(address)!)))
            throw new InvalidOperationException("P2P Visitors/Connect 要求有效的服务名、主体、端口和本机回环监听地址");

        if (peer.Socks5 is < 0 or > 65535 || peer.Socks5 != 0 && exits.Length * servers.Length > 64)
            throw new InvalidOperationException("P2P Socks5 需要 1–65535 的本机监听端口，且出口数乘以节点数不超过 64");

        if (peer.StunServers.Length > 8 || peer.StunServers.Any(server =>
            !server.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || server.Length > 256 || server.Contains('\0')))
            throw new InvalidOperationException("P2P StunServers 最多接受 8 个 stun: URI");

        if (peer.DirectUdpPort != 0 && peer.DirectUdpPort is <= 1024 or > 65535)
            throw new InvalidOperationException("P2P DirectUdpPort 必须为 0 或处于 1025–65535");

        if (peer.DirectTcpPort is < 0 or > 65535
            || peer.DirectTcpEndpoint != null && (peer.DirectTcpPort == 0
                || !IPEndPoint.TryParse(peer.DirectTcpEndpoint, out var endpoint) || endpoint.Port == 0
                || !PeerUdpHub.IsCandidate(endpoint.Address)))
            throw new InvalidOperationException("P2P DirectTcpEndpoint 要求有效的 IP:端口和非零 DirectTcpPort");
    }

    /// <summary>要么固定一个 LocalPort，要么给出一段访客可选的 Ports 白名单。</summary>
    private static bool HasPorts(PeerServiceOptions service)
        => service.Ports is null ? PeerProtocol.IsPort(service.LocalPort) : PeerPortPolicy.Parse(service.Ports) != null;

    private async Task RunProviderAsync(DefaultClientConfig config, SuiDaoServer server, PeerServiceOptions service, bool direct,
        int index, CancellationToken token)
    {
        var sessions = new HashSet<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await providerSessions.WaitAsync(token);
                ClientWebSocket? socket = null;
                var transferred = false;
                try
                {
                    socket = await ConnectAsync(server, config.Token, service.Name, null, service.AllowVisitors, direct, token);
                    var mode = await PeerProtocol.ReceiveTextAsync(socket, token);
                    if (mode is "direct" or "relay")
                    {
                        sessions.RemoveWhere(task => task.IsCompleted);
                        sessions.Add(RunProviderSessionAsync(socket, service, config.P2P, mode == "direct", index, token));
                        transferred = true;
                        continue;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "P2P 服务 {Service} 注册中断", service.Name);
                }
                finally
                {
                    if (!transferred)
                    {
                        socket?.Dispose();
                        providerSessions.Release();
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        finally { await Task.WhenAll(sessions); }
    }

    private async Task RunProviderSessionAsync(ClientWebSocket socket, PeerServiceOptions service,
        PeerOptions options, bool direct, int index, CancellationToken token)
    {
        using (socket)
        {
            try { await RunSessionAsync(socket, options, service.Name, direct, service, null, null, index, token); }
            catch (Exception ex) { logger.LogDebug(ex, "P2P 服务 {Service} 会话结束", service.Name); }
            finally { providerSessions.Release(); }
        }
    }

    private async Task RunVisitorAsync(DefaultClientConfig config, PeerChannel channel, PeerVisitorOptions visitor,
        CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var address = NetUtil.Unmap(IPAddress.Parse(visitor.ListenAddress))!;
        using var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(address, visitor.ListenPort));
        listener.Listen(128);
        logger.LogInformation("P2P 访客 {Provider}/{Service} 在 {Address}:{Port} 监听",
            visitor.Provider, visitor.Service, visitor.ListenAddress, visitor.ListenPort);
        var target = NewTarget(visitor.RemotePort);
        var active = new HashSet<Task>();
        using var permits = new SemaphoreSlim(256);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await permits.WaitAsync(lifetime.Token);
                Socket local;
                try { local = await listener.AcceptAsync(lifetime.Token); }
                catch { permits.Release(); throw; }
                active.RemoveWhere(task => task.IsCompleted);
                active.Add(ForwardVisitorAsync(local, channel.Path, target, permits, config.P2P.LogConnectionPath, lifetime.Token));
            }
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(active).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task MaintainVisitorAsync(DefaultClientConfig config, SuiDaoServer server, PeerChannel channel,
        bool direct, int index, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = await ConnectAsync(server, config.Token, channel.Service, channel.Provider, null, direct, token);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                var mode = await PeerProtocol.ReceiveTextAsync(socket, handshake.Token);
                if (mode is "direct" or "relay")
                    await RunSessionAsync(socket, config.P2P, channel.Name,
                        mode == "direct", null, channel.Path, null, index, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogDebug(ex, "P2P 访客 {Provider}/{Service} 在 {Server}:{Port} 的会话中断",
                    channel.Provider, channel.Service, server.ServerAddr, server.ServerPort);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1000)), token);
        }
    }

    /// <summary>
    /// L3 组网总控：领取目录、创建虚拟网卡、按成员维护通道，成员表变化时整套重建。
    /// 只要客户端还在跑，本任务就不结束——其他任务结束即停止整个 PeerClient，
    /// 而 L3 建不起来不应该影响已经在用的 Web / TCP 穿透。
    /// </summary>
    private async Task RunL3Async(DefaultClientConfig config, SuiDaoServer[] servers, bool direct, CancellationToken token)
    {
        // 网段和台账是服务端的全局状态，只向主节点领取
        using var directory = new PeerNetworkDirectory(servers[0], config.Token, logger);
        PeerNetworkRoster? roster = null;
        var retry = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            roster ??= await directory.FetchAsync(token);
            if (roster is null)
            {
                // 服务端没开组网时按刻钟轮询，管理员打开后本端能自动跟上；网络故障则退避到分钟级
                await Task.Delay(directory.Blocked ? BlockedRetry : retry, token);
                if (!directory.Blocked)
                    retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, MaxRetrySeconds));
                continue;
            }

            retry = TimeSpan.FromSeconds(1);
            (roster, retry) = await RunNetworkAsync(config, servers, direct, roster, directory, token);
        }
    }

    /// <summary>跑一套组网，直到成员表变化或本端停用；返回下一份目录（为 null 时重新领取）和下次尝试前的等待。</summary>
    private async Task<(PeerNetworkRoster? Roster, TimeSpan Delay)> RunNetworkAsync(DefaultClientConfig config,
        SuiDaoServer[] servers, bool direct, PeerNetworkRoster roster, PeerNetworkDirectory directory, CancellationToken token)
    {
        var device = PeerTun.Open(roster, config.P2P, logger);
        if (device is null)
            return (null, DeviceRetry);

        await using var tun = device;
        await using var network = new PeerNetwork(roster, device, logger);
        // 网卡上已有本端地址才占得住它的 53 端口；关掉 L3Dns 时网卡只搬 IP 包，完全不碰解析
        await using var dns = config.P2P.L3Dns ? PeerDns.Start(roster, network.VirtualIp, logger) : null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var changed = new TaskCompletionSource<PeerNetworkRoster>(TaskCreationOptions.RunContinuationsAsynchronously);
        var members = network.Peers;
        var links = new List<Task>();
        foreach (var peer in members)
        {
            if (IsDialer(roster.Self, peer))
            {
                // 每对成员只由编号较小的一侧开流：两侧都开时后到的一条会被当成重复通道丢弃，
                // 结果两边各留一条单向通道，谁都不通
                var channel = new PeerChannel(peer, PeerProtocol.IpService, servers.Length);
                links.AddRange(servers.Select((server, index) =>
                    MaintainVisitorAsync(config, server, channel, direct, index, lifetime.Token)));
                links.Add(MaintainIpChannelAsync(channel.Path, peer, network, config.P2P.LogConnectionPath, lifetime.Token));
            }
            else
            {
                links.AddRange(servers.Select((server, index) =>
                    PublishIpSlotAsync(config, server, peer, network, direct, index, lifetime.Token)));
            }
        }

        links.Add(WatchDirectoryAsync(directory, roster, changed, lifetime.Token));
        var pump = network.RunAsync(lifetime.Token);
        try
        {
            logger.LogInformation("P2P 组网已启用：{Device} = {Address}（{Cidr}），成员 {Peers}",
                device.Name, network.VirtualIp, roster.Cidr, members.Length == 0 ? "无" : string.Join(", ", members));
            if (await Task.WhenAny(pump, changed.Task) != changed.Task)
            {
                if (!token.IsCancellationRequested)
                    logger.LogWarning("虚拟网卡 {Device} 已停止收发，稍后重建", device.Name);
                return (null, DeviceRetry);
            }

            return (changed.Task.Result, TimeSpan.FromSeconds(1));
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(links).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>按服务端给出的新鲜度刷新目录，成员表变了就通知上层重建组网。</summary>
    private async Task WatchDirectoryAsync(PeerNetworkDirectory directory, PeerNetworkRoster current,
        TaskCompletionSource<PeerNetworkRoster> changed, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(directory.RefreshAfter, token);
            var roster = await directory.FetchAsync(token);
            if (roster != null && !SameRoster(current, roster))
                changed.TrySetResult(roster);
        }
    }

    /// <summary>为主动连进来的成员占一个席位：一个席位只服务一个访客，断开后重新排队。</summary>
    private async Task PublishIpSlotAsync(DefaultClientConfig config, SuiDaoServer server, string peerId,
        PeerNetwork network, bool direct, int index, CancellationToken token)
    {
        var sessions = new HashSet<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await providerSessions.WaitAsync(token);
                ClientWebSocket? socket = null;
                var transferred = false;
                try
                {
                    socket = await ConnectAsync(server, config.Token, PeerProtocol.IpService, null, [peerId], direct, token);
                    var mode = await PeerProtocol.ReceiveTextAsync(socket, token);
                    if (mode is "direct" or "relay")
                    {
                        sessions.RemoveWhere(task => task.IsCompleted);
                        sessions.Add(RunIpSessionAsync(socket, network, config.P2P, direct, index, token));
                        transferred = true;
                        continue;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "P2P 组网席位 {Peer} 注册中断", peerId);
                }
                finally
                {
                    if (!transferred)
                    {
                        socket?.Dispose();
                        providerSessions.Release();
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        finally { await Task.WhenAll(sessions); }
    }

    private async Task RunIpSessionAsync(ClientWebSocket socket, PeerNetwork network, PeerOptions options,
        bool direct, int index, CancellationToken token)
    {
        using (socket)
        {
            try { await RunSessionAsync(socket, options, PeerProtocol.IpService, direct, null, null, network, index, token); }
            catch (Exception ex) { logger.LogDebug(ex, "P2P 组网会话结束"); }
            finally { providerSessions.Release(); }
        }
    }

    /// <summary>开一条长驻组网流交给泵；流结束后隔一秒重来，空档里由内核自行重传。</summary>
    private async Task MaintainIpChannelAsync(PeerPath path, string peerId, PeerNetwork network,
        bool logConnectionPath, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            MuxStream? stream = null;
            try
            {
                stream = await OpenVisitorStreamAsync(path, network.NewTarget(), logConnectionPath, token);
                if (stream != null)
                    await network.ServeAsync(peerId, stream, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // 会话被拆掉时流可能直接报错，这里只留日志，重连由下面的循环负责
                logger.LogDebug(ex, "P2P 组网到 {Peer} 的通道中断", peerId);
                if (stream != null)
                    await stream.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    /// <summary>虚拟地址和成员表都没变就不用重建网卡。</summary>
    private static bool SameRoster(PeerNetworkRoster left, PeerNetworkRoster right)
        => string.Equals(left.Cidr, right.Cidr, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Self, right.Self, StringComparison.OrdinalIgnoreCase)
            && left.Peers.Length == right.Peers.Length
            && left.Peers.All(entry => string.Equals(Array.Find(right.Peers,
                other => string.Equals(other.Id, entry.Id, StringComparison.OrdinalIgnoreCase))?.Ip,
                entry.Ip, StringComparison.OrdinalIgnoreCase));

    /// <summary>每对成员固定由编号较小的一侧负责拨组网速流，另一侧只占席位。</summary>
    internal static bool IsDialer(string self, string peer)
        => string.Compare(self, peer, StringComparison.OrdinalIgnoreCase) < 0;

    private async Task RunSocks5Async(DefaultClientConfig config, PeerSocks5.Exit[] exits,
        Dictionary<string, PeerChannel> channels, CancellationToken token)
    {
        // 两个回环族都监听，应用填 localhost 不会因为解析到 ::1 而连不上
        var listeners = new List<Task> { RunSocks5ListenerAsync(config, IPAddress.Loopback, exits, channels, token) };
        if (Socket.OSSupportsIPv6)
            listeners.Add(RunSocks5ListenerAsync(config, IPAddress.IPv6Loopback, exits, channels, token));
        await Task.WhenAll(listeners);
    }

    private async Task RunSocks5ListenerAsync(DefaultClientConfig config, IPAddress address,
        PeerSocks5.Exit[] exits, Dictionary<string, PeerChannel> channels, CancellationToken token)
    {
        using var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(address, config.P2P.Socks5));
        }
        catch (SocketException ex) when (!address.Equals(IPAddress.Loopback))
        {
            // 监听不了 ::1 不算故障，127.0.0.1 已经在工作
            logger.LogDebug("P2P SOCKS5 跳过 {Address} 监听 Socket错误={SocketError}", address, ex.SocketErrorCode);
            return;
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"P2P SOCKS5 无法监听 {address}:{config.P2P.Socks5}（{ex.SocketErrorCode}）");
        }

        listener.Listen(128);
        if (address.Equals(IPAddress.Loopback))
            logger.LogInformation("P2P SOCKS5 出口已就绪：把代理设为 {Address}:{Port}，目标写 服务名.提供端ID.ft",
                address, config.P2P.Socks5);

        var active = new HashSet<Task>();
        using var permits = new SemaphoreSlim(256);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await permits.WaitAsync(token);
                Socket local;
                try { local = await listener.AcceptAsync(token); }
                catch { permits.Release(); throw; }
                active.RemoveWhere(task => task.IsCompleted);
                active.Add(HandleSocks5Async(local, exits, channels, config.P2P.LogConnectionPath, permits, token));
            }
        }
        finally
        {
            await Task.WhenAll(active).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task HandleSocks5Async(Socket local, PeerSocks5.Exit[] exits, Dictionary<string, PeerChannel> channels,
        bool logConnectionPath, SemaphoreSlim permits, CancellationToken token)
    {
        using (local)
        {
            try
            {
                var (host, port) = await PeerSocks5.ReadRequestAsync(local, token);
                if (!PeerSocks5.TryResolve(host, exits, out var index)
                    || !channels.TryGetValue(PeerChannel.Key(exits[index].Provider, exits[index].Service), out var channel))
                {
                    await PeerSocks5.WriteHostUnreachableAsync(local, token);
                    return;
                }

                MuxStream? stream;
                try { stream = await OpenVisitorStreamAsync(channel.Path, NewTarget(port), logConnectionPath, token); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException && !token.IsCancellationRequested)
                {
                    await PeerSocks5.WriteRefusedAsync(local, token);
                    return;
                }

                if (stream == null)
                {
                    await PeerSocks5.WriteRefusedAsync(local, token);
                    return;
                }

                await PeerSocks5.WriteSuccessAsync(local, token);
                await using (stream)
                    await BridgeAsync(local, stream, token);
            }
            catch (Exception ex) { logger.LogDebug(ex, "P2P SOCKS5 连接结束"); }
            finally { permits.Release(); }
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(SuiDaoServer server, string credential, string service,
        string? provider, string[]? visitors, bool direct, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            socket.Options.RemoteCertificateValidationCallback = TunnelCertificate.CreateCallback(server, logger);
            socket.Options.SetRequestHeader(ProtocolConst.FASTTUNNEL_VERSION, AssemblyUtility.GetVersion().ToString());
            socket.Options.SetRequestHeader(ProtocolConst.FASTTUNNEL_TOKEN, credential);
            socket.Options.SetRequestHeader(PeerProtocol.Service, service);
            if (provider != null)
                socket.Options.SetRequestHeader(PeerProtocol.Provider, provider);
            if (visitors != null)
                socket.Options.SetRequestHeader(PeerProtocol.Visitors, string.Join(',', visitors));
            if (direct)
                socket.Options.SetRequestHeader(PeerProtocol.Direct, PeerProtocol.DirectVersion);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var uri = new UriBuilder("wss", server.ServerAddr, server.ServerPort, PeerProtocol.Path).Uri;
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private async Task RunSessionAsync(ClientWebSocket socket, PeerOptions options, string serviceName, bool direct,
        PeerServiceOptions? service, PeerPath? path, PeerNetwork? network, int index, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var directLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var transport = new PeerWebSocket(socket);
        // 直连的探测由拨号方发起：提供端和组网被动方都只应答
        var visitor = service is null && network is null;
        var udpControl = direct && udp != null ? new PeerDirectControl(visitor) : null;
        var tcpControl = direct && tcp != null ? new PeerDirectControl(visitor) : null;
        var relay = CreateMux(transport.Pipe, service, network, logger);
        var relayIo = transport.RunAsync(message =>
        {
            if (message.StartsWith('T'))
                tcpControl?.Receive(message[1..]);
            else
                udpControl?.Receive(message);
        }, lifetime.Token);
        var relayMux = relay.RunAsync();
        path?.SetRelay(index, relay);
        logger.LogInformation("P2P WSS 中继已就绪");
        var udpMux = udpControl == null ? Task.CompletedTask
            : MaintainDirectAsync(udpControl, transport, options, serviceName, service, network, path, index, false,
                lifetime.Token, directLifetime.Token);
        var tcpMux = tcpControl == null ? Task.CompletedTask
            : MaintainDirectAsync(tcpControl, transport, options, serviceName, service, network, path, index, true,
                lifetime.Token, directLifetime.Token);
        try { await await Task.WhenAny(relayIo, relayMux); }
        finally
        {
            path?.SetRelay(index, null);
            if (transport.PolicyClosed)
                directLifetime.Cancel();
            else
                directLifetime.CancelAfter(DirectDisconnectGrace);
            lifetime.Cancel();
            await relay.DisposeAsync();
            await Task.WhenAll(relayIo, relayMux, udpMux, tcpMux).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task MaintainDirectAsync(PeerDirectControl control, PeerWebSocket relay,
        PeerOptions options, string serviceName, PeerServiceOptions? service, PeerNetwork? network, PeerPath? path,
        int index, bool useTcp, CancellationToken token, CancellationToken directToken)
    {
        if (service != null || network != null)
        {
            await foreach (var attempt in control.ReadAttemptsAsync(token))
                if (await RunDirectAttemptAsync(attempt, relay, options, serviceName, service, network, null,
                    index, useTcp, token, directToken) == null)
                    return;
            return;
        }

        var delay = 2;
        while (!token.IsCancellationRequested)
        {
            var connected = await RunDirectAttemptAsync(control.Start(), relay, options, serviceName, null,
                network, path, index, useTcp, token, directToken);
            if (connected == null)
                return;
            await Task.Delay(TimeSpan.FromSeconds(delay + Random.Shared.NextDouble()), token);
            delay = connected.Value ? 2 : Math.Min(delay * 2, 30);
        }
    }

    private async Task<bool?> RunDirectAttemptAsync(PeerDirectControl.Attempt attempt, PeerWebSocket relay,
        PeerOptions options, string serviceName, PeerServiceOptions? service, PeerNetwork? network, PeerPath? path,
        int index, bool useTcp, CancellationToken token, CancellationToken directToken)
    {
        var connected = false;
        var transport = useTcp ? "TCP/TLS" : "UDP";
        var diagnostics = new PeerAttemptLog(logger, transport, serviceName, service == null && network == null, index + 1, attempt.Generation);
        try
        {
            if (useTcp)
                await PeerTcpConnection.RunAsync(tcp!, attempt, relay, tcpEndpoint, service == null && network == null,
                    diagnostics, UseTransportAsync, token, directToken);
            else
                await PeerUdpConnection.RunAsync(udp!, attempt, relay, options.StunServers, discoveryNodes, gateway,
                    service == null && network == null, diagnostics, UseTransportAsync, token, directToken);
        }
        catch (PeerPathException ex) when (ex.Reason == "no_tcp_candidates" && !token.IsCancellationRequested)
        {
            diagnostics.Failed(ex);
            return null;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            diagnostics.Failed(ex);
        }
        finally
        {
            attempt.Signals.Writer.TryComplete();
        }
        return connected;

        async Task UseTransportAsync(PeerPipe pipe, Task wire)
        {
            connected = true;
            var mux = CreateMux(pipe, service, network, logger);
            var loop = mux.RunAsync();
            path?.SetDirect(index, useTcp, mux);
            logger.LogInformation("P2P {Transport} 直连已就绪，新 TCP 连接使用直连", transport);
            try { await await Task.WhenAny(wire, loop); }
            finally
            {
                path?.SetDirect(index, useTcp, null);
                await mux.DisposeAsync();
                await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal static MuxSession CreateMux(IDuplexPipe transport, PeerServiceOptions? service, PeerNetwork? network, ILogger logger)
    {
        if (service == null && network == null)
            return new MuxSession(transport, isServer: true, logger);

        return new MuxSession(transport, isServer: false, logger, async (stream, target) =>
        {
            if (network != null)
            {
                // 组网速流只认目录里成员自己声明的虚拟地址，认领成功后整条流交给泵
                if (!network.TryAdmit(target, out var peerId))
                {
                    logger.LogWarning("P2P 组网拒绝 {Host} 的通道：地址不在组网目录里", target.Host);
                    await stream.DisposeAsync();
                    return;
                }

                await network.ServeAsync(peerId, stream, stream.ConnectionClosed);
                return;
            }

            await using var mux = stream;
            if (!TryPickPort(service, target, out var port))
            {
                logger.LogWarning("P2P 拒绝访问本机端口 {Port}：不在 Share 放行范围内，或访客端过旧未指定端口", target.Port);
                return;
            }

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(stream.ConnectionClosed);
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            // 主机名只取提供端自己的配置，放开的只有端口；否则一条凭据就能横向扫内网。
            using var socket = await DnsSocketFactory.ConnectAsync(service.LocalHost, port, connect.Token);
            await BridgeAsync(socket, stream, stream.ConnectionClosed);
        });
    }

    /// <summary>这条流该连到提供端的哪个端口。未放行、或旧版访客端只带占位端口时返回 false。</summary>
    internal static bool TryPickPort(PeerServiceOptions service, SwapRequest target, out int port)
    {
        var policy = PeerPortPolicy.For(service);
        port = policy.FixedPort ?? target.Port;
        return (policy.FixedPort is not null || target.Port != PrivateStream.Port) && policy.Allows(port);
    }

    /// <summary>访客开流时带的目标；未写远端端口时保留占位端口，由提供端按自己的 LocalPort 连接。</summary>
    internal static SwapRequest NewTarget(int remotePort) => new()
    {
        Id = PrivateStream.Id,
        Host = PrivateStream.Host,
        Port = remotePort == 0 ? PrivateStream.Port : remotePort,
    };

    private async Task ForwardVisitorAsync(Socket local, PeerPath path, SwapRequest target, SemaphoreSlim permits,
        bool logConnectionPath, CancellationToken token)
    {
        using (local)
        {
            try
            {
                var stream = await OpenVisitorStreamAsync(path, target, logConnectionPath, token);
                if (stream == null)
                    return;
                await using (stream)
                    await BridgeAsync(local, stream, token);
            }
            catch (Exception ex) { logger.LogDebug(ex, "P2P 访客 TCP 连接结束"); }
            finally { permits.Release(); }
        }
    }

    /// <summary>按 UDP 直连 → TCP/TLS 直连 → WSS 中继的顺序开一条流；一条都开不出时返回 null。</summary>
    private async Task<MuxStream?> OpenVisitorStreamAsync(PeerPath path, SwapRequest target,
        bool logConnectionPath, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var routes = await path.GetAsync(timeout.Token);
        for (var i = 0; i < routes.Length; i++)
        {
            var route = routes[i];
            using var open = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            if (i < routes.Length - 1)
                open.CancelAfter(TimeSpan.FromMilliseconds(750));

            MuxStream stream;
            try { stream = await route.Session.OpenStreamAsync(target, open.Token); }
            catch (Exception ex) when (i < routes.Length - 1 && !timeout.IsCancellationRequested
                && ex is IOException or OperationCanceledException or InvalidOperationException)
            {
                continue;
            }

            logger.Log(logConnectionPath ? LogLevel.Information : LogLevel.Debug,
                "P2P TCP 连接使用 {Path}", route.Transport);
            return stream;
        }

        return null;
    }

    internal static async Task BridgeAsync(Socket socket, MuxStream stream, CancellationToken token)
    {
        socket.NoDelay = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, stream.ConnectionClosed);
        using var network = new NetworkStream(socket, ownsSocket: false);
        var fromLocal = UploadAsync();
        var toLocal = DownloadAsync();
        try
        {
            await await Task.WhenAny(fromLocal, toLocal);
            await Task.WhenAll(fromLocal, toLocal);
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(fromLocal, toLocal).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task UploadAsync()
        {
            await network.CopyToAsync(stream, PeerProtocol.ChunkSize, lifetime.Token);
            await stream.CompleteOutputAsync();
        }

        async Task DownloadAsync()
        {
            await stream.Input.CopyToAsync(network, lifetime.Token);
            socket.Shutdown(SocketShutdown.Send);
        }
    }

    /// <summary>到「提供端/服务」的一条通道：连接器维护 PeerPath，本机监听端口与 SOCKS5 出口共用它。</summary>
    private sealed class PeerChannel(string provider, string service, int servers)
    {
        internal string Provider { get; } = provider;
        internal string Service { get; } = service;
        internal PeerPath Path { get; } = new(servers);
        internal string Name => Key(Provider, Service);

        internal static string Key(string provider, string service) => provider + "/" + service;
    }

    private sealed class PeerPath(int count)
    {
        private readonly object gate = new();
        private readonly MuxSession?[] relays = new MuxSession?[count];
        private readonly MuxSession?[] direct = new MuxSession?[count];
        private readonly MuxSession?[] tcpDirect = new MuxSession?[count];
        private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void SetRelay(int index, MuxSession? session)
        {
            lock (gate)
            {
                relays[index] = session;
                if (session == null)
                {
                    direct[index] = null;
                    tcpDirect[index] = null;
                    if (ready.Task.IsCompleted && !relays.Any(IsOpen))
                        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                    ready.TrySetResult();
            }
        }

        internal void SetDirect(int index, bool useTcp, MuxSession? session)
        {
            lock (gate)
                (useTcp ? tcpDirect : direct)[index] = IsOpen(relays[index]) ? session : null;
        }

        internal async Task<(MuxSession Session, string Transport)[]> GetAsync(CancellationToken token)
        {
            while (true)
            {
                Task waiting;
                lock (gate)
                {
                    if (relays.Any(IsOpen))
                    {
                        var routes = direct.Select(session => (Session: session, Transport: "UDP 直连"))
                            .Concat(tcpDirect.Select(session => (Session: session, Transport: "TCP/TLS 直连")))
                            .Concat(relays.Select(session => (Session: session, Transport: "WSS")))
                            .Where(route => IsOpen(route.Session))
                            .Select(route => (route.Session!, route.Transport)).ToArray();
                        if (routes.Length != 0)
                            return routes;
                    }

                    if (ready.Task.IsCompleted)
                        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiting = ready.Task;
                }
                await waiting.WaitAsync(token);
            }
        }

        private static bool IsOpen(MuxSession? session) => session is { IsClosed: false };
    }
}
