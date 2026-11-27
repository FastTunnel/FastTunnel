using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Tests.Support;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

public class PeerUdpTests
{
    [Fact]
    public void EncryptedPacketsRejectTamperingAndReplay()
    {
        var (a, b, forward, backward) = Keys();
        using var outgoing = new PeerUdpPacket(forward, backward, a, b);
        using var incoming = new PeerUdpPacket(backward, forward, b, a);
        var data = outgoing.Write(PeerPacketType.Data, 12, [4, 5, 6]);
        var plaintext = new byte[PeerUdpPacket.MaxSize];
        data[^1] ^= 1;
        Assert.False(incoming.TryRead(data, plaintext, out _, out _));
        data[^1] ^= 1;
        Assert.True(incoming.TryRead(data, plaintext, out var type, out var sequence));
        Assert.Equal(PeerPacketType.Data, type);
        Assert.Equal(12UL, sequence);
        Assert.Equal(new byte[] { 4, 5, 6 }, plaintext[9..12]);
        Assert.False(incoming.TryRead(data, plaintext, out _, out _));
    }

    [Theory]
    [InlineData("203.0.113.12")]
    [InlineData("2001:db8::32")]
    public async Task StunDiscoveryUsesSharedSocketAndMatchesTransaction(string ip)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hub = new PeerUdpHub(0);
        using var stun = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var lookup = hub.DiscoverAsync((IPEndPoint)stun.Client.LocalEndPoint!, timeout.Token);
        var request = await stun.ReceiveAsync(timeout.Token);
        var advertised = new IPEndPoint(IPAddress.Parse(ip), 43210);
        var response = CreateStunResponse(request.Buffer, advertised);
        response[8] ^= 1;
        await stun.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        response[8] ^= 1;
        await stun.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        Assert.Equal(advertised, await lookup);
    }

    [Theory]
    [InlineData("203.0.113.12")]
    [InlineData("2001:db8::32")]
    public void CoordinatorDiscoveryNeverAmplifiesRequests(string ip)
    {
        var source = new IPEndPoint(IPAddress.Parse(ip), 43210);
        var response = new byte[44];
        Assert.Equal(0, PeerStun.WriteCoordinatorResponse(PeerStun.CreateRequest(), source, response));

        var request = PeerStun.CreateRequest(coordinator: true);
        var size = PeerStun.WriteCoordinatorResponse(request, source, response);
        Assert.InRange(size, 1, request.Length);
        Assert.Equal(source, PeerStun.ReadMappedAddress(response.AsSpan(0, size), request));
    }

    [Fact]
    public async Task CoordinatorAnswersOnItsDefaultBinPort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int port;
        using (var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;

        var options = new TestOptionsMonitor<DefaultServerConfig>(new() { EnableP2P = true, BinPort = port });
        using var service = new PeerDiscoveryService(options, NullLogger<PeerDiscoveryService>.Instance);
        await using var hub = new PeerUdpHub(0);
        await service.StartAsync(timeout.Token);
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var mapped = await hub.DiscoverAsync(endpoint, timeout.Token, coordinator: true);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, hub.Port), mapped);
        }
        finally { await service.StopAsync(timeout.Token); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GatewayMapperUsesPcpOrFallsBackToNatPmp(bool pcp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gateway = new PeerGatewayMapper.Gateway(IPAddress.Loopback, (IPEndPoint)socket.Client.LocalEndPoint!);
        var external = IPAddress.Parse("203.0.113.41");
        var request = PeerGatewayMapper.TryMapAsync(gateway, 41234, null, timeout.Token);
        var first = await socket.ReceiveAsync(timeout.Token);
        Assert.Equal(2, first.Buffer[0]);
        if (pcp)
        {
            first.Buffer[1] = 129;
            BinaryPrimitives.WriteUInt16BigEndian(first.Buffer.AsSpan(42), 43210);
            external.MapToIPv6().TryWriteBytes(first.Buffer.AsSpan(44), out _);
            await socket.SendAsync(first.Buffer, first.RemoteEndPoint, timeout.Token);
        }
        else
        {
            var pcpUnsupported = new byte[24];
            pcpUnsupported[0] = 2;
            pcpUnsupported[1] = 129;
            pcpUnsupported[3] = 1;
            await socket.SendAsync(pcpUnsupported, first.RemoteEndPoint, timeout.Token);
            var addressRequest = await socket.ReceiveAsync(timeout.Token);
            Assert.Equal(new byte[2], addressRequest.Buffer);
            byte[] address = [0, 128, 0, 0, 0, 0, 0, 0, .. external.GetAddressBytes()];
            await socket.SendAsync(address, addressRequest.RemoteEndPoint, timeout.Token);
            var mapRequest = await socket.ReceiveAsync(timeout.Token);
            Assert.Equal(1, mapRequest.Buffer[1]);
            var mapResponse = new byte[16];
            mapResponse[1] = 129;
            mapRequest.Buffer.AsSpan(4, 2).CopyTo(mapResponse.AsSpan(8));
            BinaryPrimitives.WriteUInt16BigEndian(mapResponse.AsSpan(10), 43210);
            BinaryPrimitives.WriteUInt32BigEndian(mapResponse.AsSpan(12), 300);
            await socket.SendAsync(mapResponse, mapRequest.RemoteEndPoint, timeout.Token);
        }

        var result = Assert.IsType<PeerGatewayMapper.Mapping>(await request);
        Assert.Equal(new IPEndPoint(external, 43210), result.External);
        Assert.Equal(pcp, result.Nonce != null);
        Assert.True(result.Expires > result.RenewAfter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateRaceContinuesWhenFirstRespondingPathStopsConfirming(bool dropFinalConfirmation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hub = new PeerUdpHub(0);
        var local = new IPEndPoint(IPAddress.Loopback, hub.Port);
        using var failing = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var working = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var (a, b, forward, backward) = Keys();
        using var visitor = new PeerUdpPacket(forward, backward, a, b);
        using var provider = new PeerUdpPacket(backward, forward, b, a);
        var log = new PeerAttemptLog(NullLogger.Instance, "UDP", "test", true, 1, 1);
        var path = PeerUdpConnection.FindPathAsync(hub, visitor, hub.Register(a),
            new ConcurrentDictionary<IPEndPoint, byte>(), visitor: true, timeout.Token, log);

        try
        {
            await SendAsync(failing, PeerPacketType.ProbeReply);
            await ReceiveAsync(failing, PeerPacketType.Select);
            ulong? oldCommit = null;
            if (dropFinalConfirmation)
            {
                await SendAsync(failing, PeerPacketType.Selected);
                oldCommit = await ReceiveAsync(failing, PeerPacketType.Commit);
            }
            await SendAsync(working, PeerPacketType.ProbeReply);
            await ReceiveAsync(working, PeerPacketType.Select);
            await SendAsync(working, PeerPacketType.Selected);
            var commit = await ReceiveAsync(working, PeerPacketType.Commit);
            if (oldCommit is { } stale)
            {
                Assert.True(commit > stale);
                await SendAsync(failing, PeerPacketType.Committed, stale);
            }
            await SendAsync(working, PeerPacketType.Committed, commit);
            Assert.Equal(working.Client.LocalEndPoint, await path);
            Assert.Equal(dropFinalConfirmation ? 1 : 0, log.CommitTimeouts);
        }
        finally
        {
            timeout.Cancel();
            await ((Task)path).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task SendAsync(UdpClient socket, PeerPacketType type, ulong number = 0)
            => await socket.SendAsync(provider.Write(type, number, []), local, timeout.Token);

        async Task<ulong> ReceiveAsync(UdpClient socket, PeerPacketType expected)
        {
            var plaintext = new byte[PeerUdpPacket.MaxSize];
            while (true)
            {
                var packet = await socket.ReceiveAsync(timeout.Token);
                if (provider.TryRead(packet.Buffer, plaintext, out var type, out var number) && type == expected)
                    return number;
            }
        }
    }

    [Fact]
    public async Task CandidateNegotiationWorksOverIpv6()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var a = new PeerUdpHub(0);
        await using var b = new PeerUdpHub(0);
        var aEndpoint = new IPEndPoint(IPAddress.IPv6Loopback, a.Port);
        var bEndpoint = new IPEndPoint(IPAddress.IPv6Loopback, b.Port);
        var (aId, bId, forward, backward) = Keys();
        using var aCodec = new PeerUdpPacket(forward, backward, aId, bId);
        using var bCodec = new PeerUdpPacket(backward, forward, bId, aId);
        var left = PeerUdpConnection.FindPathAsync(a, aCodec, a.Register(aId),
            new ConcurrentDictionary<IPEndPoint, byte> { [bEndpoint] = 0 }, true, timeout.Token);
        var right = PeerUdpConnection.FindPathAsync(b, bCodec, b.Register(bId),
            new ConcurrentDictionary<IPEndPoint, byte> { [aEndpoint] = 0 }, false, timeout.Token);
        var results = await Task.WhenAll(left, right);
        Assert.Equal(new[] { bEndpoint, aEndpoint }, results);
    }

    [Theory]
    [InlineData(false, "no_remote_candidates")]
    [InlineData(true, "no_probe_response")]
    public async Task UdpFailureDistinguishesMissingCandidatesFromMissingReplies(bool candidate, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await using var hub = new PeerUdpHub(0);
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var (a, b, forward, backward) = Keys();
        using var codec = new PeerUdpPacket(forward, backward, a, b);
        var candidates = new ConcurrentDictionary<IPEndPoint, byte>();
        if (candidate)
            candidates.TryAdd((IPEndPoint)silent.Client.LocalEndPoint!, 0);
        var failure = await Assert.ThrowsAsync<PeerPathException>(() =>
            PeerUdpConnection.FindPathAsync(hub, codec, hub.Register(a), candidates, true, timeout.Token));
        Assert.Equal(reason, failure.Reason);
    }

    [Fact]
    public async Task ReliableStreamRecoversDroppedAndReorderedDatagrams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var a = new PeerUdpHub(0);
        await using var b = new PeerUdpHub(0);
        using var bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var aEndpoint = new IPEndPoint(IPAddress.Loopback, a.LocalCandidates().First().Port);
        var bEndpoint = new IPEndPoint(IPAddress.Loopback, b.LocalCandidates().First().Port);
        var via = (IPEndPoint)bridge.Client.LocalEndPoint!;
        var (aId, bId, forward, backward) = Keys();
        using var aCodec = new PeerUdpPacket(forward, backward, aId, bId);
        using var bCodec = new PeerUdpPacket(backward, forward, bId, aId);
        var left = new PeerUdpTransport(a, via, aCodec, a.Register(aId), visitor: true);
        var right = new PeerUdpTransport(b, via, bCodec, b.Register(bId), visitor: false);
        var leftLoop = left.RunAsync(timeout.Token);
        var rightLoop = right.RunAsync(timeout.Token);
        var network = ForwardAsync();

        try
        {
            var payload = RandomNumberGenerator.GetBytes(48 * 1024);
            await left.Pipe.Output.WriteAsync(payload, timeout.Token);
            Assert.Equal(payload, await TestPipes.ReadExactAsync(right.Pipe.Input, payload.Length, timeout.Token));
        }
        finally
        {
            timeout.Cancel();
            await Task.WhenAll(leftLoop, rightLoop, network).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task ForwardAsync()
        {
            var sent = 0;
            var acks = 0;
            while (true)
            {
                var packet = await bridge.ReceiveAsync(timeout.Token);
                var fromLeft = aEndpoint.Equals(packet.RemoteEndPoint);
                if (fromLeft && packet.Buffer.Length > PeerUdpPacket.Overhead && ++sent == 1
                    || !fromLeft && ++acks == 3)
                    continue;

                await bridge.SendAsync(packet.Buffer, fromLeft ? bEndpoint : aEndpoint, timeout.Token);
            }
        }
    }

    private static (Guid A, Guid B, byte[] Forward, byte[] Backward) Keys()
        => (Guid.NewGuid(), Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    private static byte[] CreateStunResponse(byte[] request, IPEndPoint mapped)
    {
        var address = mapped.Address.GetAddressBytes();
        var response = new byte[28 + address.Length];
        request.CopyTo(response, 0);
        response[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), (ushort)(response.Length - 20));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), (ushort)(address.Length + 4));
        response[25] = (byte)(address.Length == 4 ? 1 : 2);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(mapped.Port ^ 0x2112));
        for (var i = 0; i < address.Length; i++)
            response[28 + i] = (byte)(address[i] ^ request[4 + i]);
        return response;
    }
}
