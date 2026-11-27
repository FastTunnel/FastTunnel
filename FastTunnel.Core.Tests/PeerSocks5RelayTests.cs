using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Tests.Support;
using FastTunnel.Core.Tls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// SOCKS5 出口的端到端往返：真实服务端 + 两个 PeerClient（提供端与访客端），
/// 访客侧应用只连本机 SOCKS 端口，靠主机名和端口挑到提供端内网的服务。
/// </summary>
public class PeerSocks5RelayTests
{
    [Fact]
    public async Task Socks5ExitReachesSharedPortsAndRefusesOthers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var directory = new TempDirectory();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory.Path,
        });
        builder.Logging.ClearProviders();
        var server = new DefaultServerConfig
        {
            EnableP2P = true,
            P2PDiscoveryPort = 0,
            Clients = [
                new() { Id = "home", Token = "provider-token" },
                new() { Id = "guest", Token = "visitor-token" },
            ],
        };
        var monitor = new TestOptionsMonitor<DefaultServerConfig>(server);
        var certificates = new ServerCertificateProvider(monitor, new TestHostEnvironment(directory.Path),
            NullLogger<ServerCertificateProvider>.Instance);
        using var certificate = certificates.GetCertificate();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(certificate)));
        builder.Services.AddFastTunnelServer(builder.Configuration.GetSection("FastTunnel"));
        builder.Services.AddSingleton<IOptionsMonitor<DefaultServerConfig>>(monitor);
        await using var app = builder.Build();
        app.UseFastTunnelServer();
        await app.StartAsync(timeout.Token);
        var address = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        var node = new SuiDaoServer
        {
            Protocol = "wss",
            ServerAddr = address.Host,
            ServerPort = address.Port,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            P2PDiscoveryPort = 0,
        };

        // 提供端的「内网服务」：一个回显监听，端口由访客在 SOCKS 请求里给出
        using var origin = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        origin.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        origin.Listen(16);
        var originPort = ((IPEndPoint)origin.LocalEndPoint!).Port;
        var echo = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                Socket peer;
                try { peer = await origin.AcceptAsync(timeout.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (peer)
                    {
                        await using var stream = new NetworkStream(peer, ownsSocket: false);
                        var buffer = new byte[16];
                        var read = await stream.ReadAsync(buffer, timeout.Token);
                        if (read > 0)
                            await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }
                });
            }
        });

        var socksPort = SocksProbe.FreePort();
        var provider = StartPeer(new DefaultClientConfig
        {
            Token = "provider-token",
            Server = node,
            P2P = new PeerOptions
            {
                EnableDirect = false,
                Share = { ["dev"] = "*@guest", ["tight"] = "9000-9001@guest" },
            },
        });
        var visitor = StartPeer(new DefaultClientConfig
        {
            Token = "visitor-token",
            Server = node,
            P2P = new PeerOptions
            {
                EnableDirect = false,
                Socks5 = socksPort,
                Socks5Via = ["home/dev", "home/tight"],
            },
        });
        using (provider)
        using (visitor)
        {
            await provider.StartAsync(timeout.Token);
            await visitor.StartAsync(timeout.Token);

            byte[]? echoed = null;
            var started = Stopwatch.StartNew();
            while (echoed == null && started.Elapsed < TimeSpan.FromSeconds(25))
            {
                try
                {
                    echoed = await SocksProbe.RoundTripAsync(socksPort, "dev.home.ft", originPort,
                        new byte[] { 7, 8, 9 }, timeout.Token);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    await Task.Delay(300, timeout.Token);
                }
            }

            Assert.Equal(new byte[] { 7, 8, 9 }, echoed);

            // 不在放行范围内的端口，提供端接了流也不拨号，访客只看到连接被切断
            var refused = await SocksProbe.RoundTripAsync(socksPort, "tight.home.ft", originPort,
                new byte[] { 1 }, timeout.Token);
            Assert.Empty(refused);
        }

        await app.StopAsync(timeout.Token);
        timeout.Cancel();
        await echo;
    }

    private static PeerClient StartPeer(DefaultClientConfig config) => new(
        new TestOptionsMonitor<DefaultClientConfig>(config), NullLogger<PeerClient>.Instance);
}
