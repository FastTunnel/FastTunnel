using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using FastTunnel.Core.Sockets;

namespace FastTunnel.Core.P2P;

// One socket is used for discovery, NAT checks and data, including when several
// sessions use the same configured UDP port.
internal sealed class PeerUdpHub : IAsyncDisposable
{
    private readonly UdpClient udp;
    private readonly CancellationTokenSource stopped = new();
    private readonly Task receiver;
    private readonly ConcurrentDictionary<Guid, Channel<UdpReceiveResult>> routes = new();
    private readonly ConcurrentDictionary<string, Channel<UdpReceiveResult>> stun = new();

    internal PeerUdpHub(int port)
    {
        udp = NetUtil.BindUdp(IPAddress.IPv6Any, port);
        receiver = ReceiveAsync();
    }

    internal bool Supports(IPAddress address)
        => (udp.Client.AddressFamily == AddressFamily.InterNetworkV6 && udp.Client.DualMode) || address.AddressFamily == udp.Client.AddressFamily;

    internal int Port => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

    internal IEnumerable<IPEndPoint> LocalCandidates()
    {
        var port = Port;
        return InterfaceAddresses().Where(Supports).Take(24).Select(address => new IPEndPoint(address, port));
    }

    internal static IEnumerable<IPAddress> InterfaceAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(IsCandidate)
            .Distinct();

    internal static bool IsCandidate(IPAddress address)
        => !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
            && !address.Equals(IPAddress.Broadcast) && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast
            && (!address.IsIPv4MappedToIPv6 && (address.AddressFamily != AddressFamily.InterNetwork
                || address.GetAddressBytes()[0] is > 0 and < 224));

    internal ChannelReader<UdpReceiveResult> Register(Guid route)
    {
        var packets = Channel.CreateBounded<UdpReceiveResult>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        if (!routes.TryAdd(route, packets))
            throw new InvalidOperationException("Duplicate P2P UDP route");
        return packets.Reader;
    }

    internal void Unregister(Guid route)
    {
        if (routes.TryRemove(route, out var packets))
            packets.Writer.TryComplete();
    }

    internal async Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint endpoint, CancellationToken token)
    {
        try
        {
            if (udp.Client.AddressFamily == AddressFamily.InterNetworkV6
                && endpoint.AddressFamily == AddressFamily.InterNetwork)
                endpoint = new IPEndPoint(endpoint.Address.MapToIPv6(), endpoint.Port);
            await udp.Client.SendToAsync(packet, SocketFlags.None, endpoint, token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostUnreachable
            or SocketError.NetworkUnreachable or SocketError.ConnectionReset or SocketError.AddressNotAvailable
            or SocketError.ConnectionRefused or SocketError.AccessDenied)
        {
            // An individual candidate may be unreachable. Other candidates can still succeed.
        }
    }

    internal async Task<IPEndPoint?> DiscoverAsync(IPEndPoint server, CancellationToken token, bool coordinator = false)
    {
        var request = PeerStun.CreateRequest(coordinator);
        var id = Convert.ToHexString(request.AsSpan(8, 12));
        var responses = Channel.CreateBounded<UdpReceiveResult>(4);
        if (!stun.TryAdd(id, responses))
            return null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(1100));
            for (var retry = 0; retry < 3; retry++)
            {
                await SendAsync(request, server, deadline.Token);
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                interval.CancelAfter(TimeSpan.FromMilliseconds(200 << retry));
                try
                {
                    while (await responses.Reader.WaitToReadAsync(interval.Token))
                        while (responses.Reader.TryRead(out var response))
                            if (response.RemoteEndPoint.Equals(server)
                                && PeerStun.ReadMappedAddress(response.Buffer, request) is { } mapped)
                                return mapped;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally { stun.TryRemove(id, out _); }
        return null;
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(stopped.Token); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                    or SocketError.ConnectionRefused or SocketError.MessageSize) { continue; }

                var endpoint = received.RemoteEndPoint;
                if (endpoint.Address.IsIPv4MappedToIPv6)
                    received = new UdpReceiveResult(received.Buffer, new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port));
                var content = received.Buffer;
                if (PeerStun.IsResponse(content) && stun.TryGetValue(Convert.ToHexString(content.AsSpan(8, 12)), out var responses))
                    responses.Writer.TryWrite(received);
                else if (content.Length is >= PeerUdpPacket.Overhead and <= PeerUdpPacket.MaxSize
                    && routes.TryGetValue(new Guid(content.AsSpan(0, 16)), out var packets))
                    packets.Writer.TryWrite(received);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        finally
        {
            foreach (var route in routes.Values)
                route.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopped.Cancel();
        try { await receiver; }
        finally
        {
            udp.Dispose();
            stopped.Dispose();
        }
    }
}
