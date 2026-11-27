using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Json;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Security;
using FastTunnel.Core.Tests.Support;
using FastTunnel.Core.Tls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FastTunnel.Core.Tests;

public class PeerNetworkTests
{
    [Fact]
    public void CidrOnlyAcceptsPrivateRangesAndReservesNetworkAndBroadcast()
    {
        var cgnat = PeerNetworkCidr.Parse("100.64.0.0/16");
        Assert.NotNull(cgnat);
        Assert.Equal("100.64.0.0/16", cgnat!.Text);
        Assert.Equal(65534, cgnat.Capacity);
        Assert.True(cgnat.Contains(IPAddress.Parse("100.64.0.1")));
        Assert.True(cgnat.Contains(IPAddress.Parse("100.64.255.254")));
        // 网络地址与广播地址不发放
        Assert.False(cgnat.Contains(IPAddress.Parse("100.64.0.0")));
        Assert.False(cgnat.Contains(IPAddress.Parse("100.64.255.255")));
        Assert.False(cgnat.Contains(IPAddress.Parse("100.65.0.1")));
        Assert.False(cgnat.Contains(IPAddress.Parse("fd7a:115c:a1e0::1")));
        Assert.Equal("255.255.0.0", cgnat.NetMask().ToString());

        // 主机位写错也归一化成网段本身
        Assert.Equal("192.168.7.0/24", PeerNetworkCidr.Parse("192.168.7.99/24")!.Text);
        Assert.Equal(254, PeerNetworkCidr.Parse("192.168.7.0/24")!.Capacity);
        Assert.Null(PeerNetworkCidr.Parse("8.8.8.0/24"));
        Assert.Null(PeerNetworkCidr.Parse("172.32.0.0/16"));
        Assert.Null(PeerNetworkCidr.Parse("100.128.0.0/16"));
        Assert.Null(PeerNetworkCidr.Parse("10.0.0.1"));
        Assert.Null(PeerNetworkCidr.Parse("10.0.0.0/31"));
        Assert.Null(PeerNetworkCidr.Parse(null));
    }

    [Fact]
    public void VirtualAddressesAscendAndSurviveReload()
    {
        var file = Path.Combine(Path.GetTempPath(), "ft-peers-" + Guid.NewGuid().ToString("N") + ".json");
        var cidr = PeerNetworkCidr.Parse("100.64.0.0/24")!;
        try
        {
            var registry = NewRegistry(file);
            Redeem(registry, "laptop");
            Redeem(registry, "desktop");
            Redeem(registry, "nas");

            var roster = registry.RefreshNetwork(cidr);
            Assert.Equal(["100.64.0.1", "100.64.0.2", "100.64.0.3"], roster.Select(x => x.Ip).ToArray());
            Assert.Equal(["laptop", "desktop", "nas"], roster.Select(x => x.Id).ToArray());

            // 重复拉取不再改动已发的地址
            Assert.Equal(roster.Select(x => x.Ip).ToArray(), registry.RefreshNetwork(cidr).Select(x => x.Ip).ToArray());

            // 撤销的设备不进目录，但地址保留：恢复后仍是同一个 IP
            registry.Revoke("desktop");
            Assert.Equal(["laptop", "nas"], registry.RefreshNetwork(cidr).Select(x => x.Id).ToArray());
            Assert.True(registry.Restore("desktop"));
            Assert.Equal("100.64.0.2", Assert.Single(registry.RefreshNetwork(cidr).Where(x => x.Id == "desktop")).Ip);

            // 删除后腾出的地址会发给新设备，已分配的不会漂移
            Assert.True(registry.Remove("desktop"));
            Redeem(registry, "tv");
            Assert.Equal(["100.64.0.1", "100.64.0.2", "100.64.0.3"],
                registry.RefreshNetwork(cidr).Select(x => x.Ip).ToArray());
            Assert.Equal("tv", registry.RefreshNetwork(cidr)[1].Id);

            // 地址写在台账文件里，重启后不漂移
            var reloaded = NewRegistry(file);
            Assert.Equal(["laptop", "tv", "nas"], reloaded.RefreshNetwork(cidr).Select(x => x.Id).ToArray());

            // 换网段后旧地址越界，重新按序发放
            Assert.Equal(["10.0.0.1", "10.0.0.2", "10.0.0.3"],
                reloaded.RefreshNetwork(PeerNetworkCidr.Parse("10.0.0.0/24")!).Select(x => x.Ip).ToArray());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExhaustedCidrFailsLoudlyInsteadOfReusingAddresses()
    {
        var registry = NewRegistry(null);
        var tiny = PeerNetworkCidr.Parse("192.168.9.0/29")!;
        Assert.Equal(6, tiny.Capacity);
        for (var index = 0; index < 6; index++)
            Redeem(registry, "peer" + index);

        var roster = registry.RefreshNetwork(tiny);
        Assert.Equal(6, roster.Count);
        Assert.Equal("192.168.9.6", roster[^1].Ip);

        Redeem(registry, "late");
        var error = Assert.Throws<InvalidOperationException>(() => registry.RefreshNetwork(tiny));
        Assert.Contains("192.168.9.0/29", error.Message);
    }

    [Fact]
    public void ClientKeepsL3DisabledUnlessConfigured()
    {
        using var config = new ConfigurationManager();
        config["FastTunnel:P2P:Share:ssh"] = "22@laptop";
        Assert.False(config.GetSection("FastTunnel").Get<DefaultClientConfig>()!.P2P.EnableL3);

        config["FastTunnel:P2P:EnableL3"] = "true";
        var peer = config.GetSection("FastTunnel").Get<DefaultClientConfig>()!.P2P;
        Assert.True(peer.EnableL3);

        // 只开 L3 也要让 PeerClient 起来，否则虚拟网卡永远不会配置
        Assert.True(new PeerOptions { EnableL3 = true }.HasEndpoints);
        Assert.False(new PeerOptions().HasEndpoints);
    }

    [Fact]
    public void ServerDefaultsKeepL3Off()
    {
        var server = new DefaultServerConfig();
        Assert.False(server.EnableP2P);
        Assert.False(server.P2PNetwork.Enabled);
        Assert.Equal("100.64.0.0/16", server.P2PNetwork.Cidr);
        Assert.Equal(60, server.P2PNetwork.DirectorySeconds);
    }

    [Fact]
    public void IpPacketParsesRoutingRelevantFields()
    {
        var tcp = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, "100.64.0.1", "100.64.0.2",
            [0x04, 0xB0, 0x01, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x60, 0x02, 0x20, 0x00]);
        Assert.True(PeerIpPacket.TryParse(tcp, out var packet));
        Assert.Equal("100.64.0.1", packet.Source.ToString());
        Assert.Equal("100.64.0.2", packet.Destination.ToString());
        Assert.Equal(20, packet.HeaderLength);
        Assert.Equal(tcp.Length, packet.TotalLength);
        Assert.False(packet.IsFragment);
        Assert.True(packet.TryPorts(out var source, out var target));
        Assert.Equal(1200, source);
        Assert.Equal(443, target);
        Assert.Equal(tcp.Length - 20, packet.Payload.Length);

        // 带选项的首部（IHL=6）：载荷从第 24 字节开始，前 4 字节算首部选项而不是端口
        var options = TestPackets.Ipv4(PeerIpPacket.ProtocolUdp, "10.0.0.1", "10.0.0.2",
            [0, 0, 0, 0, 0x00, 0x50, 0x00, 0x35, 0x00, 0x08]);
        options[0] = 0x46;
        TestPackets.Sign(options);
        Assert.True(PeerIpPacket.TryParse(options, out var wide));
        Assert.Equal(24, wide.HeaderLength);
        Assert.Equal(6, wide.Payload.Length);
        Assert.True(wide.TryPorts(out var dnsSource, out var dnsTarget));
        Assert.Equal(80, dnsSource);
        Assert.Equal(53, dnsTarget);

        // 分片包（MF=1）没有可依赖的端口
        var fragment = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, "10.0.0.1", "10.0.0.2", [0x04, 0xB8, 0x01, 0xBB]);
        fragment[6] = 0x20;
        TestPackets.Sign(fragment);
        Assert.True(PeerIpPacket.TryParse(fragment, out var piece));
        Assert.True(piece.IsFragment);
    }

    [Fact]
    public void IpPacketRejectsGarbageInsteadOfTrustingPeer()
    {
        var tcp = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, "100.64.0.1", "100.64.0.2", [0x04, 0xB8, 0x01, 0xBB]);

        // 非 IPv4
        var ipv6 = (byte[])tcp.Clone();
        ipv6[0] = 0x60;
        Assert.False(PeerIpPacket.TryParse(ipv6, out _));
        // 太短
        Assert.False(PeerIpPacket.TryParse(tcp[..19], out _));
        // IHL 小于 20
        var tiny = (byte[])tcp.Clone();
        tiny[0] = 0x44;
        TestPackets.Sign(tiny);
        Assert.False(PeerIpPacket.TryParse(tiny, out _));
        // 以太网填充不影响解析，但总长度越界必须拒绝
        var padded = new byte[tcp.Length + 3];
        tcp.CopyTo(padded, 0);
        Assert.True(PeerIpPacket.TryParse(padded, out var withPadding));
        Assert.Equal(tcp.Length, withPadding.TotalLength);
        var shorted = (byte[])tcp.Clone();
        shorted[2] = 0;
        shorted[3] = 200;
        TestPackets.Sign(shorted);
        Assert.False(PeerIpPacket.TryParse(shorted, out _));
        // 校验和被改坏
        var broken = (byte[])tcp.Clone();
        broken[10] ^= 0xFF;
        Assert.False(PeerIpPacket.TryParse(broken, out _));
        // ICMP 不暴露端口
        var icmp = TestPackets.Ipv4(1, "100.64.0.1", "100.64.0.2", [0x08, 0x00, 0x00, 0x00]);
        Assert.True(PeerIpPacket.TryParse(icmp, out var ping));
        Assert.False(ping.TryPorts(out _, out _));
    }

    [Fact]
    public async Task RosterEndpointStaysInvisibleUntilServerEnablesL3()
    {
        using var server = await RosterServer.StartAsync(enableL3: false);

        // 没开 L3 的部署：这个地址根本不存在，客户端只会看到一条停用日志
        Assert.Equal(HttpStatusCode.NotFound, (await server.SendAsync("device-token")).StatusCode);

        server.Config.P2PNetwork.Enabled = true;
        var peers = server.Peers;
        server.Enroll("laptop", server.Config.P2PInviteKey);

        var answer = await server.SendAsync("device-token");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType);
        var roster = await answer.Content.ReadFromJsonAsync(
            AppJsonContext.Default.PeerNetworkRoster, server.Timeout.Token);
        Assert.Equal("100.64.0.0/16", roster!.Cidr);
        Assert.Equal("laptop", roster.Self);
        Assert.Equal("100.64.0.1", Assert.Single(roster.Peers).Ip);
        Assert.Equal("laptop", roster.Peers[0].Name);

        // 手工配置但没入网的凭据进不了组网目录
        Assert.Equal(HttpStatusCode.Forbidden, (await server.SendAsync("other-token")).StatusCode);

        // 没有凭据就是 401，不泄露成员表
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.SendAsync("")).StatusCode);

        // 设备被吊销后领不到地址：目录直接 403，客户端只会留下一条日志
        peers.Revoke("laptop");
        server.Config.P2PNetwork.Cidr = "10.0.0.0/24";
        Assert.Equal(HttpStatusCode.Forbidden, (await server.SendAsync("device-token")).StatusCode);
    }

    /// <summary>
    /// 跑着真实 Kestrel（自签 TLS）的组网目录端点，路径、证书、Token 头都与客户端一致。
    /// </summary>
    internal sealed class RosterServer : IDisposable
    {
        private RosterServer(WebApplication app, DefaultServerConfig config, HttpClient http,
            TempDirectory directory, X509Certificate2 certificate, string fingerprint)
        {
            App = app;
            Config = config;
            Http = http;
            Directory = directory;
            Certificate = certificate;
            Fingerprint = fingerprint;
            Url = new Uri(new Uri(app.Urls.Single()), PeerProtocol.NetworkPath);
            Peers = app.Services.GetRequiredService<PeerRegistry>();
        }

        internal WebApplication App { get; }

        internal DefaultServerConfig Config { get; }

        internal PeerRegistry Peers { get; }

        internal Uri Url { get; }

        /// <summary>服务端自签证书的 SHA-256 指纹，客户端用它固定身份。</summary>
        internal string Fingerprint { get; }

        internal CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(30));

        private HttpClient Http { get; }

        private TempDirectory Directory { get; }

        /// <summary>Kestrel 用的自签证书必须活到服务停止，否则握手会拿到已释放的句柄。</summary>
        private X509Certificate2 Certificate { get; }

        internal static async Task<RosterServer> StartAsync(bool enableL3 = true, string cidr = "100.64.0.0/16")
        {
            using var certificateDirectory = new TempDirectory();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = certificateDirectory.Path });
            builder.Logging.ClearProviders();
            var config = new DefaultServerConfig
            {
                EnableP2P = true,
                P2PDiscoveryPort = 0,
                P2PInviteKey = new string('A', 64),
                P2PEnrollment = { StateFile = Path.Combine(certificateDirectory.Path, "peers.json") },
                P2PNetwork = { Enabled = enableL3, Cidr = cidr },
                Clients = [new() { Id = "laptop", Token = "device-token" }, new() { Id = "outsider", Token = "other-token" }],
            };
            var monitor = new TestOptionsMonitor<DefaultServerConfig>(config);
            var certificates = new ServerCertificateProvider(monitor, new TestHostEnvironment(certificateDirectory.Path),
                NullLogger<ServerCertificateProvider>.Instance);
            var certificate = certificates.GetCertificate();
            var fingerprint = TunnelCertificate.GetFingerprint(certificate);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.UseHttps(certificate)));
            builder.Services.AddFastTunnelServer(builder.Configuration.GetSection("FastTunnel"));
            builder.Services.AddSingleton<IOptionsMonitor<DefaultServerConfig>>(monitor);
            var app = builder.Build();
            app.UseFastTunnelServer();
            using (var start = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await app.StartAsync(start.Token);
            }

            return new RosterServer(app, config, new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote != null
                    && TunnelCertificate.GetFingerprint(remote) == fingerprint,
            })
            { Timeout = TimeSpan.FromSeconds(15) }, certificateDirectory, certificate, fingerprint);
        }

        /// <summary>指向本节点的客户端配置，Protocol=wss 让目录走 https。</summary>
        internal SuiDaoServer Node()
            => new()
            {
                ServerAddr = Url.Host,
                ServerPort = Url.Port,
                Protocol = "wss",
                CertificateFingerprint = Fingerprint,
            };

        internal async Task<HttpResponseMessage> SendAsync(string token)
        {
            // HttpRequestMessage 是一次性的，每次请求都要新建
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Add(ProtocolConst.FASTTUNNEL_TOKEN, token);
            return await Http.SendAsync(request, Timeout.Token);
        }

        /// <summary>用入网码把一台设备登记进台账，它才有资格领虚拟地址。</summary>
        internal void Enroll(string hostname, string key)
        {
            var ticket = Peers.CreateTicket(key, null, 5);
            Assert.NotNull(Peers.Redeem(key, ticket.Code, hostname));
        }

        public void Dispose()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            App.StopAsync(timeout.Token).GetAwaiter().GetResult();
            App.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Timeout.Dispose();
            Http.Dispose();
            Certificate.Dispose();
            Directory.Dispose();
        }
    }

    private static PeerRegistry NewRegistry(string? file)
        => new(NullLogger<PeerRegistry>.Instance, file);

    private static void Redeem(PeerRegistry registry, string hostname)
    {
        var key = new string('C', 64);
        var ticket = registry.CreateTicket(key, null, 5);
        Assert.NotNull(registry.Redeem(key, ticket.Code, hostname));
    }
}
