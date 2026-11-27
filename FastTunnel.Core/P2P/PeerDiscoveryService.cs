using System.Net;
using System.Net.Sockets;
using FastTunnel.Core.Config;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.P2P;

internal sealed class PeerDiscoveryService(IOptionsMonitor<DefaultServerConfig> options, ILogger<PeerDiscoveryService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        UdpClient? udp = null;
        var port = 0;
        var failedPort = 0;
        try
        {
            do
            {
                var config = options.CurrentValue;
                var requested = config.EnableP2P ? config.P2PDiscoveryPort ?? config.BinPort : 0;
                if (requested != port)
                {
                    udp?.Dispose();
                    udp = null;
                    port = 0;
                }
                if (udp == null && requested != 0)
                {
                    try
                    {
                        udp = NetUtil.BindUdp(IPAddress.IPv6Any, requested);
                        port = requested;
                        failedPort = 0;
                        logger.LogInformation("P2P 地址发现监听 UDP {Port}", port);
                    }
                    catch (Exception ex) when (ex is SocketException or ArgumentOutOfRangeException)
                    {
                        if (failedPort != requested)
                            logger.LogWarning(ex, "P2P 地址发现无法监听 UDP {Port}", requested);
                        failedPort = requested;
                    }
                }

                if (udp != null)
                {
                    using var refresh = CancellationTokenSource.CreateLinkedTokenSource(token);
                    refresh.CancelAfter(TimeSpan.FromSeconds(2));
                    try { await ServeAsync(udp.Client, refresh.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        finally { udp?.Dispose(); }
    }

    private static async Task ServeAsync(Socket socket, CancellationToken token)
    {
        var request = new byte[45];
        var response = new byte[44];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var source = await socket.ReceiveFromAsync(request.AsMemory(), SocketFlags.None, any, token);
                var size = PeerStun.WriteCoordinatorResponse(request.AsSpan(0, source.ReceivedBytes),
                    (IPEndPoint)source.RemoteEndPoint, response);
                if (size != 0)
                    await socket.SendToAsync(response.AsMemory(0, size), SocketFlags.None, source.RemoteEndPoint, token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset
                or SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.AccessDenied)
            {
                // Malformed UDP packets and unreachable senders do not stop discovery.
            }
        }
    }
}
