using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using FastTunnel.Core.Config;

namespace FastTunnel.Core.P2P;

internal static class PeerUdpConnection
{
    internal static async Task RunAsync(PeerUdpHub hub, PeerDirectControl.Attempt attempt,
        PeerWebSocket relay, string[] stunServers, SuiDaoServer[] coordinators, PeerGatewayMapper? gateway, bool visitor,
        PeerAttemptLog diagnostics, Func<PeerPipe, Task, Task> useTransport, CancellationToken token, CancellationToken directToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var wireLifetime = CancellationTokenSource.CreateLinkedTokenSource(directToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var local = Guid.NewGuid();
        var incoming = hub.Register(local);
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var hello = attempt.Prefix + "K" + local.ToString("N") + Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
            if (visitor)
                await relay.SendSignalAsync(attempt.Prefix, deadline.Token);
            await relay.SendSignalAsync(hello, deadline.Token);

            var remoteHello = await attempt.Signals.Reader.ReadAsync(deadline.Token);
            if (remoteHello.Length is < 34 or > 256 || remoteHello[0] != 'K'
                || !Guid.TryParseExact(remoteHello.AsSpan(1, 32), "N", out var remote) || remote == local)
                throw new IOException("Invalid P2P UDP handshake");

            using var remoteEcdh = ECDiffieHellman.Create();
            var publicKey = Convert.FromBase64String(remoteHello[33..]);
            remoteEcdh.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || remoteEcdh.KeySize != 256)
                throw new IOException("Invalid P2P public key");

            var secret = ecdh.DeriveKeyFromHash(remoteEcdh.PublicKey, HashAlgorithmName.SHA256);
            var salt = new byte[32];
            (visitor ? local : remote).TryWriteBytes(salt);
            (visitor ? remote : local).TryWriteBytes(salt.AsSpan(16));
            var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, "FastTunnel/UDP/4"u8.ToArray());
            CryptographicOperations.ZeroMemory(secret);
            using var codec = new PeerUdpPacket(keys.AsSpan(visitor ? 0 : 32, 32),
                keys.AsSpan(visitor ? 32 : 0, 32), local, remote);
            CryptographicOperations.ZeroMemory(keys);

            var candidates = new ConcurrentDictionary<IPEndPoint, byte>();
            diagnostics.Stage = "udp_probe";
            var signals = ReadCandidatesAsync();
            var advertise = AdvertiseAsync();
            try
            {
                var endpoint = await FindPathAsync(hub, codec, incoming, candidates, visitor, deadline.Token, diagnostics);
                deadline.Token.ThrowIfCancellationRequested();
                var transport = new PeerUdpTransport(hub, endpoint, codec, incoming, visitor);
                diagnostics.Stage = "udp_data";
                var wire = transport.RunAsync(wireLifetime.Token);
                try { await useTransport(transport.Pipe, wire); }
                finally
                {
                    lifetime.Cancel();
                    wireLifetime.Cancel();
                    await wire.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            finally
            {
                lifetime.Cancel();
                await Task.WhenAll(signals, advertise).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            async Task ReadCandidatesAsync()
            {
                try
                {
                    await foreach (var message in attempt.Signals.Reader.ReadAllAsync(lifetime.Token))
                    {
                        if (candidates.Count >= 48 || message.Length is < 2 or > 128 || message[0] != 'C'
                            || !IPEndPoint.TryParse(message.AsSpan(1), out var endpoint)
                            || endpoint.Port == 0 || !PeerUdpHub.IsCandidate(endpoint.Address) || !hub.Supports(endpoint.Address))
                            continue;
                        if (candidates.TryAdd(endpoint, 0))
                            diagnostics.Remote++;
                    }
                }
                finally
                {
                    // A newer attempt supersedes this wire. Losing signaling alone does not.
                    if (!lifetime.IsCancellationRequested)
                        wireLifetime.Cancel();
                    lifetime.Cancel();
                }
            }

            async Task AdvertiseAsync()
            {
                var endpoints = new HashSet<IPEndPoint>();
                foreach (var endpoint in hub.LocalCandidates())
                {
                    endpoints.Add(endpoint);
                    diagnostics.Local++;
                    await relay.SendSignalAsync(attempt.Prefix + "C" + endpoint, deadline.Token);
                }

                var discoveries = stunServers.SelectMany(uri => DiscoverTasks(uri)).ToList();
                foreach (var coordinator in coordinators)
                {
                    var discoveryPort = coordinator.P2PDiscoveryPort ?? coordinator.ServerPort;
                    if (discoveryPort == 0)
                        continue;
                    var host = coordinator.ServerAddr.Trim('[', ']');
                    discoveries.Add(DiscoverAddressAsync(host, discoveryPort, AddressFamily.InterNetwork, builtIn: true));
                    if (hub.Supports(IPAddress.IPv6Loopback))
                        discoveries.Add(DiscoverAddressAsync(host, discoveryPort, AddressFamily.InterNetworkV6, builtIn: true));
                }
                try
                {
                    while (discoveries.Count != 0)
                    {
                        var next = await Task.WhenAny(discoveries);
                        discoveries.Remove(next);
                        foreach (var endpoint in await next)
                        {
                            diagnostics.Discovered++;
                            await AdvertiseEndpointAsync(endpoint);
                        }
                    }
                    if (gateway != null)
                        foreach (var endpoint in await gateway.GetCandidatesAsync(deadline.Token))
                        {
                            diagnostics.Mapped++;
                            await AdvertiseEndpointAsync(endpoint);
                        }
                }
                finally { await Task.WhenAll(discoveries).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }

                async Task AdvertiseEndpointAsync(IPEndPoint endpoint)
                {
                    if (endpoints.Add(endpoint))
                        await relay.SendSignalAsync(attempt.Prefix + "C" + endpoint, deadline.Token);
                }
            }

            IEnumerable<Task<IPEndPoint[]>> DiscoverTasks(string uri)
            {
                yield return DiscoverAsync(uri, AddressFamily.InterNetwork);
                if (hub.Supports(IPAddress.IPv6Loopback))
                    yield return DiscoverAsync(uri, AddressFamily.InterNetworkV6);
            }

            Task<IPEndPoint[]> DiscoverAsync(string uri, AddressFamily family)
            {
                if (!Uri.TryCreate("udp://" + uri[5..].TrimStart('/'), UriKind.Absolute, out var address)
                    || address.Host.Length == 0 || address.Port == 0 || address.UserInfo.Length != 0
                    || address.Query.Length != 0 || address.AbsolutePath != "/")
                    return Task.FromResult<IPEndPoint[]>([]);

                return DiscoverAddressAsync(address.DnsSafeHost, address.Port < 0 ? 3478 : address.Port, family, builtIn: false);
            }

            async Task<IPEndPoint[]> DiscoverAddressAsync(string host, int port, AddressFamily family, bool builtIn)
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host, family, deadline.Token);
                    var targets = addresses.Where(hub.Supports).Distinct().Take(2).ToArray();
                    Interlocked.Add(ref diagnostics.DiscoveryTargets, targets.Length);
                    var mapped = await Task.WhenAll(targets.Select(ip =>
                        hub.DiscoverAsync(new IPEndPoint(ip, port), deadline.Token, builtIn)));
                    return mapped.OfType<IPEndPoint>().ToArray();
                }
                catch (SocketException) { return []; }
            }
        }
        finally { hub.Unregister(local); }
    }

    internal static async Task<IPEndPoint> FindPathAsync(PeerUdpHub hub, PeerUdpPacket codec,
        ChannelReader<UdpReceiveResult> incoming, ConcurrentDictionary<IPEndPoint, byte> candidates,
        bool visitor, CancellationToken token, PeerAttemptLog? diagnostics = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var verified = new ConcurrentDictionary<IPEndPoint, byte>();
        var confirmed = new Dictionary<IPEndPoint, long>();
        var gate = new object();
        Nomination? nomination = null;
        ulong sequence = 0;
        var complete = false;
        var replies = 0;
        var commitTimeouts = 0;
        var authenticated = 0;
        var probing = ProbeAsync();
        var plaintext = new byte[PeerUdpPacket.MaxSize];
        try
        {
            await foreach (var packet in incoming.ReadAllAsync(lifetime.Token))
            {
                if (!codec.TryRead(packet.Buffer, plaintext, out var type, out var number))
                    continue;

                var source = packet.RemoteEndPoint;
                if (packet.Buffer.Length != PeerUdpPacket.Overhead)
                {
                    // A lost Ready must not strand an otherwise usable path. The
                    // first data packet will be sent again by the reliable transport.
                    if (!visitor && type == PeerPacketType.Data && source.Equals(nomination?.Endpoint))
                        return source;
                    continue;
                }

                authenticated++;
                if (type == PeerPacketType.Probe)
                    await SendAsync(PeerPacketType.ProbeReply, source);
                else if (visitor && type == PeerPacketType.ProbeReply && verified.Count < 48 && verified.TryAdd(source, 0))
                {
                    replies++;
                    await SendAsync(PeerPacketType.Select, source);
                }
                else if (visitor && type == PeerPacketType.Selected && verified.ContainsKey(source))
                {
                    lock (gate)
                        confirmed.TryAdd(source, 0);
                }
                else if (visitor && type == PeerPacketType.Committed)
                {
                    lock (gate)
                    {
                        if (nomination is not { } picked || number != picked.Number || !source.Equals(picked.Endpoint))
                            continue;
                        complete = true;
                    }
                    await SendAsync(PeerPacketType.Ready, source, number);
                    return source;
                }
                else if (!visitor && type == PeerPacketType.Select && (verified.ContainsKey(source)
                    || verified.Count < 48 && verified.TryAdd(source, 0)))
                {
                    await SendAsync(PeerPacketType.Selected, source);
                }
                else if (!visitor && type == PeerPacketType.Commit && number != 0 && verified.ContainsKey(source))
                {
                    if (nomination == null || number > nomination.Number)
                        nomination = new Nomination(source, number, 0);
                    if (number == nomination.Number && source.Equals(nomination.Endpoint))
                    {
                        if (diagnostics != null)
                            diagnostics.Stage = "udp_commit";
                        await SendAsync(PeerPacketType.Committed, source, number);
                    }
                }
                else if (!visitor && nomination is { } selected && source.Equals(selected.Endpoint)
                    && (type == PeerPacketType.Ready && number == selected.Number || type == PeerPacketType.Ping))
                    return source;
            }
            throw new IOException("P2P UDP socket closed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var reason = commitTimeouts != 0 || nomination != null ? "commit_timeout"
                : authenticated != 0 ? "selection_timeout"
                : candidates.IsEmpty ? "no_remote_candidates" : "no_probe_response";
            throw new PeerPathException(reason);
        }
        finally
        {
            lifetime.Cancel();
            await probing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (diagnostics != null)
            {
                diagnostics.ProbeReplies = replies;
                diagnostics.CommitTimeouts = commitTimeouts;
            }
        }

        async Task ProbeAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(220));
            do
            {
                Nomination? current = null;
                if (visitor)
                {
                    lock (gate)
                    {
                        if (complete)
                            return;
                        var now = Environment.TickCount64;
                        if (nomination is { } previous && now >= previous.Expires)
                        {
                            confirmed[previous.Endpoint] = now + 1500;
                            nomination = null;
                            commitTimeouts++;
                        }
                        if (nomination == null)
                        {
                            foreach (var entry in confirmed.Where(entry => entry.Value <= now).OrderBy(entry => entry.Value))
                            {
                                nomination = new Nomination(entry.Key, ++sequence, now + 880);
                                if (diagnostics != null)
                                    diagnostics.Stage = "udp_commit";
                                break;
                            }
                        }
                        current = nomination;
                    }
                }
                if (current != null)
                    await SendAsync(PeerPacketType.Commit, current.Endpoint, current.Number);

                foreach (var endpoint in candidates.Keys)
                    if (!visitor || !verified.ContainsKey(endpoint))
                        await SendAsync(PeerPacketType.Probe, endpoint);
                if (visitor)
                    foreach (var endpoint in verified.Keys)
                        await SendAsync(PeerPacketType.Select, endpoint);
            }
            while (await timer.WaitForNextTickAsync(lifetime.Token));
        }

        Task SendAsync(PeerPacketType type, IPEndPoint endpoint, ulong number = 0) =>
            hub.SendAsync(codec.Write(type, number, []), endpoint, lifetime.Token);
    }

    private sealed record Nomination(IPEndPoint Endpoint, ulong Number, long Expires);
}
