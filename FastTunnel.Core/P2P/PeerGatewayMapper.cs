using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

internal sealed class PeerGatewayMapper : IAsyncDisposable
{
    private const uint LeaseSeconds = 300;
    private readonly int port;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stop;
    private readonly Task loop;
    private readonly TaskCompletionSource firstScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Mapping[] current = [];

    internal PeerGatewayMapper(int port, ILogger logger, CancellationToken token)
    {
        this.port = port;
        this.logger = logger;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        loop = RunAsync(stop.Token);
    }

    internal async Task<IPEndPoint[]> GetCandidatesAsync(CancellationToken token)
    {
        await firstScan.Task.WaitAsync(token);
        var now = Environment.TickCount64;
        return Volatile.Read(ref current).Where(lease => lease.Expires > now).Select(lease => lease.External).ToArray();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var previous = Volatile.Read(ref current);
                var now = Environment.TickCount64;
                try
                {
                    var results = await Task.WhenAll(Gateways().Select(async gateway =>
                    {
                        var existing = Array.Find(previous, lease => lease.Gateway == gateway && lease.Expires > now);
                        if (existing != null && existing.RenewAfter > now)
                            return existing;

                        Mapping? mapped;
                        try { mapped = await TryMapAsync(gateway, port, existing, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return existing; }
                        if (mapped != null && !mapped.External.Equals(existing?.External))
                            logger.LogDebug("P2P {Protocol} 获得 UDP 网关映射",
                                mapped.Upnp != null ? "UPnP IGD" : mapped.Nonce != null ? "PCP" : "NAT-PMP");
                        return mapped ?? existing;
                    }));
                    Volatile.Write(ref current, results.OfType<Mapping>().ToArray());
                }
                catch (NetworkInformationException ex)
                {
                    logger.LogDebug(ex, "P2P 无法枚举默认网关");
                }
                finally { firstScan.TrySetResult(); }
                var untilRenew = Volatile.Read(ref current).Select(lease => lease.RenewAfter - Environment.TickCount64)
                    .DefaultIfEmpty(15000).Min();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(untilRenew, 1000, 15000)), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            firstScan.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await Task.WhenAll(Volatile.Read(ref current).Select(lease => ReleaseAsync(lease, port, deadline.Token)))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static IEnumerable<Gateway> Gateways()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(nic => nic.GetIPProperties())
            .SelectMany(properties => properties.GatewayAddresses
                .Where(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && PeerUdpHub.IsCandidate(gateway.Address))
                .SelectMany(gateway => properties.UnicastAddresses
                    .Where(local => local.Address.AddressFamily == AddressFamily.InterNetwork
                        && PeerUdpHub.IsCandidate(local.Address))
                    .Select(local => new Gateway(local.Address, new IPEndPoint(gateway.Address, 5351)))))
            .Distinct().Take(4);

    internal static async Task<Mapping?> TryMapAsync(Gateway gateway, int port, Mapping? old, CancellationToken token)
    {
        if (old?.Upnp == null)
        {
            var result = await TryPcpOrPmpAsync(gateway, port, old, token);
            if (result != null || old != null)
                return result;
        }

        var control = old?.Upnp ?? await PeerUpnp.DiscoverAsync(gateway.Local, gateway.Endpoint.Address, token);
        if (control == null)
            return null;
        var external = await PeerUpnp.MapAsync(control, gateway.Local, port, old?.External.Port, LeaseSeconds, token);
        return external != null ? NewMapping(gateway, external, LeaseSeconds, null) with { Upnp = control } : null;
    }

    private static async Task<Mapping?> TryPcpOrPmpAsync(Gateway gateway, int port, Mapping? old, CancellationToken token)
    {
        try
        {
            using var socket = OpenGateway(gateway);
            if (old == null || old.Nonce != null)
            {
                var nonce = old?.Nonce ?? RandomNumberGenerator.GetBytes(12);
                var reply = await ExchangeAsync(socket, PcpRequest(gateway.Local, port, nonce, old?.External, LeaseSeconds),
                    token);
                if (ReadPcp(reply, nonce, port) is { } pcp)
                    return NewMapping(gateway, pcp.Endpoint, pcp.Seconds, nonce);
                if (old != null)
                    return null;
            }

            var external = await ExchangeAsync(socket, new byte[2], token);
            if (external is not { Length: 12 } || !IsPmpResponse(external, 128))
                return null;

            var address = new IPAddress(external.AsSpan(8, 4));
            if (!PeerUdpHub.IsCandidate(address))
                return null;
            var response = await ExchangeAsync(socket, PmpRequest(port, old?.External.Port ?? port, LeaseSeconds), token);
            if (response is not { Length: 16 } || !IsPmpResponse(response, 129)
                || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8)) != port)
                return null;
            var externalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10));
            var seconds = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(12));
            return externalPort != 0 && seconds != 0
                ? NewMapping(gateway, new IPEndPoint(address, externalPort), seconds, null)
                : null;
        }
        catch (SocketException) { return null; }
    }

    private static Mapping NewMapping(Gateway gateway, IPEndPoint endpoint, uint seconds, byte[]? nonce)
    {
        var now = Environment.TickCount64;
        return new Mapping(gateway, endpoint, nonce, now + seconds * 1000L, now + seconds * 500L);
    }

    private static async Task ReleaseAsync(Mapping mapping, int port, CancellationToken token)
    {
        if (mapping.Expires <= Environment.TickCount64)
            return;
        if (mapping.Upnp != null)
        {
            await PeerUpnp.ReleaseAsync(mapping.Upnp, mapping.Gateway.Local, port, mapping.External.Port, token);
            return;
        }
        using var socket = OpenGateway(mapping.Gateway);
        var request = mapping.Nonce != null
            ? PcpRequest(mapping.Gateway.Local, port, mapping.Nonce, mapping.External, 0)
            : PmpRequest(port, 0, 0);
        await socket.SendAsync(request.AsMemory(), SocketFlags.None, token);
    }

    private static Socket OpenGateway(Gateway gateway)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(gateway.Local, 0));
            socket.Connect(gateway.Endpoint);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static async Task<byte[]?> ExchangeAsync(Socket socket, byte[] request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(425));
        var response = new byte[256];
        try
        {
            await socket.SendAsync(request.AsMemory(), SocketFlags.None, timeout.Token);
            var size = await socket.ReceiveAsync(response.AsMemory(), SocketFlags.None, timeout.Token);
            return response[..size];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static byte[] PcpRequest(IPAddress local, int port, byte[] nonce, IPEndPoint? external, uint seconds)
    {
        var request = new byte[60];
        request[0] = 2;
        request[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), seconds);
        local.MapToIPv6().TryWriteBytes(request.AsSpan(8, 16), out _);
        nonce.CopyTo(request, 24);
        request[36] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(40), (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(42), (ushort)(external?.Port ?? port));
        external?.Address.MapToIPv6().TryWriteBytes(request.AsSpan(44), out _);
        return request;
    }

    private static (IPEndPoint Endpoint, uint Seconds)? ReadPcp(byte[]? response, byte[] nonce, int port)
    {
        if (response is not { Length: >= 60 } || response[0] != 2 || response[1] != 129
            || response[3] != 0 || !response.AsSpan(24, 12).SequenceEqual(nonce) || response[36] != 17
            || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(40)) != port)
            return null;

        var seconds = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4));
        var externalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(42));
        var address = new IPAddress(response.AsSpan(44, 16));
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return seconds != 0 && externalPort != 0 && PeerUdpHub.IsCandidate(address)
            ? (new IPEndPoint(address, externalPort), seconds) : null;
    }

    private static byte[] PmpRequest(int port, int externalPort, uint seconds)
    {
        var request = new byte[12];
        request[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), seconds);
        return request;
    }

    private static bool IsPmpResponse(byte[] response, byte opcode)
        => response[0] == 0 && response[1] == opcode && response[2] == 0 && response[3] == 0;

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; }
        finally { stop.Dispose(); }
    }

    internal sealed record Gateway(IPAddress Local, IPEndPoint Endpoint);
    internal sealed record Mapping(Gateway Gateway, IPEndPoint External, byte[]? Nonce, long Expires, long RenewAfter)
    {
        internal PeerUpnp.Control? Upnp { get; init; }
    }
}
