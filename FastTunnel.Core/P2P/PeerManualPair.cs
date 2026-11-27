using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FastTunnel.Core.Config;
using FastTunnel.Core.Mux;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastTunnel.Core.P2P;

/// <summary>Runs a direct UDP tunnel using an invite and answer exchanged out of band.</summary>
public static class PeerManualPair
{
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Starts the provider or visitor over an optionally supplied STUN service.</summary>
    public static async Task RunAsync(bool visitor, int port, string? stunServer,
        TextReader input, TextWriter output, CancellationToken token)
    {
        if (!PeerProtocol.IsPort(port))
            throw new ArgumentOutOfRangeException(nameof(port));
        using var pairing = CancellationTokenSource.CreateLinkedTokenSource(token);
        pairing.CancelAfter(OfferLifetime);
        using var listener = visitor ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) : null;
        listener?.Bind(new IPEndPoint(IPAddress.Loopback, port));
        await using var hub = new PeerUdpHub(0);
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        PairCode? offer = null;
        if (visitor)
        {
            await output.WriteLineAsync("[1/3] 请粘贴提供端发来的整行邀请，按回车：");
            offer = await ReadCodeAsync(input, output, "offer", null, pairing.Token);
            var remaining = offer.Expires - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new IOException("P2P 邀请已过期");
            pairing.CancelAfter(remaining);
        }

        await output.WriteLineAsync(stunServer == null
            ? "正在收集局域网地址。"
            : $"正在通过 {stunServer} 查询公网地址。");
        var targets = await ResolveStunAsync(hub, stunServer, pairing.Token);
        var mapped = await Task.WhenAll(targets.Select(target => hub.DiscoverAsync(target, pairing.Token)));
        var candidates = hub.LocalCandidates().Concat(mapped.OfType<IPEndPoint>()).Distinct().Take(32).ToArray();
        await output.WriteLineAsync($"本地候选 {candidates.Length}，STUN 公网映射 {mapped.Count(endpoint => endpoint != null)}");
        if (stunServer != null && mapped.All(endpoint => endpoint == null))
            await output.WriteLineAsync("STUN 没有返回公网地址。仍会尝试局域网或可达的 IPv6；跨网失败时可换一个可达的 STUN 节点。");
        var local = new PairCode(
            visitor ? "answer" : "offer", Guid.NewGuid(), offer?.Id ?? Guid.Empty,
            offer?.Expires ?? DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.Add(OfferLifetime).ToUnixTimeSeconds()),
            key.ExportSubjectPublicKeyInfo(), candidates);
        if (!visitor)
            pairing.CancelAfter(local.Expires - DateTimeOffset.UtcNow);
        var wireInput = hub.Register(local.Id);
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(pairing.Token);
        var keepMapping = KeepMappingAsync();
        try
        {
            var localText = local.Encode();
            PairCode remote;
            if (visitor)
            {
                remote = offer!;
                await output.WriteLineAsync("[2/3] 把下一行应答完整复制给提供端：");
                await output.WriteLineAsync(localText);
                await output.WriteLineAsync("[3/3] 请让提供端粘贴应答。保持此窗口打开，客户端正在等待对端打洞。");
            }
            else
            {
                await output.WriteLineAsync("[1/3] 把下一行邀请完整复制给访客，邀请 5 分钟内有效：");
                await output.WriteLineAsync(localText);
                await output.WriteLineAsync("[2/3] 保持此窗口打开，收到访客的应答后粘贴整行并回车：");
                remote = await ReadCodeAsync(input, output, "answer", local, pairing.Token);
                var remaining = local.Expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new IOException("P2P 邀请已过期");
                pairing.CancelAfter(remaining < TimeSpan.FromSeconds(25) ? remaining : TimeSpan.FromSeconds(25));
                await output.WriteLineAsync("[3/3] 已收到应答，正在尝试 UDP 打洞。");
            }

            using var remoteKey = ECDiffieHellman.Create();
            remoteKey.ImportSubjectPublicKeyInfo(remote.PublicKey, out var consumed);
            if (consumed != remote.PublicKey.Length || remoteKey.KeySize != 256 || remote.Id == local.Id)
                throw new IOException("P2P 配对密钥无效");

            var secret = key.DeriveKeyFromHash(remoteKey.PublicKey, HashAlgorithmName.SHA256);
            var salt = SHA256.HashData(Encoding.ASCII.GetBytes(visitor
                ? remote.Encode() + "\n" + localText : localText + "\n" + remote.Encode()));
            var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, "FastTunnel/manual-UDP/1"u8.ToArray());
            CryptographicOperations.ZeroMemory(secret);
            using var codec = new PeerUdpPacket(keys.AsSpan(visitor ? 0 : 32, 32),
                keys.AsSpan(visitor ? 32 : 0, 32), local.Id, remote.Id);
            CryptographicOperations.ZeroMemory(keys);

            var endpoints = new ConcurrentDictionary<IPEndPoint, byte>(
                remote.Endpoints.Where(endpoint => hub.Supports(endpoint.Address))
                    .Select(endpoint => KeyValuePair.Create(endpoint, (byte)0)));
            var peer = await PeerUdpConnection.FindPathAsync(hub, codec, wireInput, endpoints, visitor, pairing.Token);
            pairing.Token.ThrowIfCancellationRequested();
            refresh.Cancel();
            listener?.Listen(128);
            var transport = new PeerUdpTransport(hub, peer, codec, wireInput, visitor);
            await output.WriteLineAsync(visitor ? $"P2P UDP 直连已就绪，连接 127.0.0.1:{port}"
                : $"P2P UDP 直连已就绪，已共享 127.0.0.1:{port}");
            if (visitor && OperatingSystem.IsWindows())
                await output.WriteLineAsync($"如在测试 Windows 远程桌面，请在此电脑运行 mstsc /v:127.0.0.1:{port}");
            await output.WriteLineAsync("连接期间保持此窗口打开，按 Ctrl+C 停止。");
            await RunTunnelAsync(transport, listener, port, token);
        }
        catch (PeerPathException ex) when (!token.IsCancellationRequested)
        {
            var cause = ex.Reason switch
            {
                "no_remote_candidates" => "没有对端可用的 UDP 地址",
                "no_probe_response" => "已发送探测，但对端未回应",
                "selection_timeout" => "收到探测流量，但候选路径未完成确认",
                "commit_timeout" => "已找到候选路径，但最终确认超时",
                _ => "UDP 建连未完成"
            };
            throw new IOException($"UDP 直连失败（{ex.Reason}）：{cause}。请确认两端都保持运行，并检查 UDP 防火墙或换用可达 STUN；手动模式没有中继。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && pairing.IsCancellationRequested)
        {
            throw new IOException("P2P 手动配对超时，请重新生成邀请");
        }
        finally
        {
            refresh.Cancel();
            await keepMapping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            hub.Unregister(local.Id);
        }

        async Task KeepMappingAsync()
        {
            if (targets.Length == 0)
                return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(refresh.Token))
                await Task.WhenAll(targets.Select(target => hub.DiscoverAsync(target, refresh.Token)));
        }
    }

    /// <summary>Checks the syntax of an optional STUN service address.</summary>
    public static bool IsStunAddress(string value) => TryParseStunAddress(value, out _);

    private static bool TryParseStunAddress(string value, out Uri? uri)
    {
        uri = null;
        return value.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate("udp://" + value[5..].TrimStart('/'), UriKind.Absolute, out uri)
            && uri.Host.Length != 0 && uri.Port != 0 && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
            && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static async Task<IPEndPoint[]> ResolveStunAsync(PeerUdpHub hub, string? stunServer, CancellationToken token)
    {
        if (stunServer == null)
            return [];
        if (!TryParseStunAddress(stunServer, out var uri) || uri == null)
            throw new ArgumentException("STUN 地址应使用 stun:主机:端口", nameof(stunServer));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, token);
            return addresses.Where(hub.Supports).Distinct().Take(4)
                .Select(address => new IPEndPoint(address, uri.Port < 0 ? 3478 : uri.Port)).ToArray();
        }
        catch (SocketException) { return []; }
    }

    private static async Task<PairCode> ReadCodeAsync(TextReader input, TextWriter output, string kind,
        PairCode? offer, CancellationToken token)
    {
        while (true)
        {
            var text = await Task.Run(input.ReadLine, CancellationToken.None).WaitAsync(token);
            if (text == null)
                throw new IOException("输入已关闭，请在终端运行 p2p-pair。");
            try
            {
                var code = PairCode.Decode(text, kind);
                if (offer != null && (code.OfferId != offer.Id || code.Expires != offer.Expires))
                    throw new IOException("该应答与当前邀请不匹配");
                return code;
            }
            catch (IOException ex)
            {
                await output.WriteLineAsync($"{ex.Message}。请重新粘贴当前配对的整行内容，按回车重试。");
            }
        }
    }

    private static async Task RunTunnelAsync(PeerUdpTransport transport, Socket? listener, int port, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var mux = PeerClient.CreateMux(transport.Pipe,
            listener == null ? new PeerServiceOptions { LocalPort = port } : null, null, NullLogger.Instance);
        var wire = transport.RunAsync(lifetime.Token);
        var loop = mux.RunAsync();
        var accept = listener == null ? Task.Delay(Timeout.Infinite, lifetime.Token) : AcceptAsync(listener, mux, lifetime.Token);
        try { await await Task.WhenAny(wire, loop, accept); }
        finally
        {
            lifetime.Cancel();
            await mux.DisposeAsync();
            await Task.WhenAll(wire, loop, accept).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AcceptAsync(Socket listener, MuxSession mux, CancellationToken token)
    {
        var streams = new HashSet<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                streams.RemoveWhere(task => task.IsCompleted);
                if (streams.Count >= 128)
                    await Task.WhenAny(streams).WaitAsync(token);
                streams.Add(ForwardAsync(await listener.AcceptAsync(token), mux, token));
            }
        }
        finally { await Task.WhenAll(streams).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    private static async Task ForwardAsync(Socket local, MuxSession mux, CancellationToken token)
    {
        using (local)
        {
            try
            {
                await using var stream = await mux.OpenStreamAsync(PeerClient.PrivateStream, token);
                await PeerClient.BridgeAsync(local, stream, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException) { }
        }
    }

    internal sealed record PairCode(string Kind, Guid Id, Guid OfferId, DateTimeOffset Expires,
        byte[] PublicKey, IPEndPoint[] Endpoints)
    {
        internal string Encode()
        {
            var header = string.Create(CultureInfo.InvariantCulture,
                $"FTPAIR1\n{Kind}\n{Id:N}\n{OfferId:N}\n{Expires.ToUnixTimeSeconds()}\n{Convert.ToBase64String(PublicKey)}");
            var endpoints = Endpoints.Select(endpoint => endpoint.ToString()).Order(StringComparer.Ordinal);
            return Convert.ToBase64String(Encoding.ASCII.GetBytes(header + "\n" + string.Join('\n', endpoints)));
        }

        internal static PairCode Decode(string? text, string expected)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 8192)
                throw new IOException("P2P 配对信息无效");
            string[] fields;
            try { fields = Encoding.ASCII.GetString(Convert.FromBase64String(text)).Split('\n'); }
            catch (FormatException) { throw new IOException("P2P 配对信息无效"); }
            if (fields.Length is < 7 or > 38 || fields[0] != "FTPAIR1" || fields[1] != expected
                || !Guid.TryParseExact(fields[2], "N", out var id) || id == Guid.Empty
                || !Guid.TryParseExact(fields[3], "N", out var offer)
                || (expected == "offer" ? offer != Guid.Empty : offer == Guid.Empty)
                || !long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
                || unix is <= 0 or > 253402300799L || fields[5].Length > 256)
                throw new IOException("P2P 配对信息无效或方向不正确");
            var expires = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.Add(OfferLifetime).AddSeconds(30))
                throw new IOException("P2P 配对信息已过期或两端时间不同步");

            byte[] key;
            try { key = Convert.FromBase64String(fields[5]); }
            catch (FormatException) { throw new IOException("P2P 配对密钥无效"); }
            var endpoints = new HashSet<IPEndPoint>();
            foreach (var field in fields.Skip(6))
            {
                if (!IPEndPoint.TryParse(field, out var endpoint) || endpoint.Port == 0
                    || !PeerUdpHub.IsCandidate(endpoint.Address))
                    throw new IOException("P2P 配对地址无效");
                endpoints.Add(endpoint);
            }
            return new PairCode(expected, id, offer, expires, key, endpoints.ToArray());
        }
    }
}
