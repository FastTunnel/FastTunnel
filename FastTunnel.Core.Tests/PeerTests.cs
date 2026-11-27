using System.Net;
using System.Net.WebSockets;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Tests.Support;
using FastTunnel.Core.Tls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

public class PeerTests
{
    [Fact]
    public void ManualPairingCodesCheckDirectionAndExpiration()
    {
        var offer = new PeerManualPair.PairCode("offer", Guid.NewGuid(), Guid.Empty,
            DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60),
            [1, 2, 3], [new(IPAddress.IPv6Loopback, 43210), new(IPAddress.Loopback, 34567)]);
        var text = offer.Encode();
        Assert.Equal(text, PeerManualPair.PairCode.Decode("  " + text + "  ", "offer").Encode());
        Assert.Throws<IOException>(() => PeerManualPair.PairCode.Decode(text, "answer"));
        Assert.Throws<IOException>(() => PeerManualPair.PairCode.Decode(
            (offer with { Expires = DateTimeOffset.UtcNow.AddSeconds(-5) }).Encode(), "offer"));
    }

    [Fact]
    public void InviteTokensAreSignedRoleBoundAndExpire()
    {
        var key = new string('A', 64);
        var pair = PeerInvites.Create(key, "rdp.test", 30);
        var now = DateTimeOffset.UtcNow;
        var provider = PeerInvites.Verify(pair.ProviderToken, key, now)!.Value;
        var visitor = PeerInvites.Verify(pair.VisitorToken, key, now)!.Value;
        Assert.True(provider.Provider);
        Assert.False(visitor.Provider);
        Assert.Equal(provider.Pair, visitor.Pair);
        Assert.Equal("rdp.test", provider.Service);
        Assert.Equal(pair.ProviderId, provider.Id);
        Assert.Equal(pair.VisitorId, visitor.Id);
        Assert.Null(PeerInvites.Verify(pair.ProviderToken, new string('B', 64), now));
        Assert.Null(PeerInvites.Verify(pair.ProviderToken.Replace(".p.", ".v."), key, now));
        Assert.Null(PeerInvites.Verify(pair.VisitorToken, key, pair.Expires));
    }

    [Fact]
    public void ShorthandBindsLoopbackAndPreservesDetailedEntries()
    {
        using var config = new ConfigurationManager();
        config["FastTunnel:P2P:Share:ssh"] = "22@laptop, desktop";
        config["FastTunnel:P2P:Connect:home/ssh"] = "2222";
        config["FastTunnel:P2P:Services:0:Name"] = "api";
        var peer = config.GetSection("FastTunnel").Get<DefaultClientConfig>()!.P2P;

        Assert.True(peer.HasEndpoints);
        var share = Assert.Single(peer.GetServices(), service => service.Name == "ssh");
        Assert.Equal("127.0.0.1", share.LocalHost);
        Assert.Equal(22, share.LocalPort);
        Assert.Equal(new[] { "laptop", "desktop" }, share.AllowVisitors);
        Assert.Contains(peer.GetServices(), service => service.Name == "api");
        var visitor = Assert.Single(peer.GetVisitors());
        Assert.Equal("home", visitor.Provider);
        Assert.Equal("ssh", visitor.Service);
        Assert.Equal("127.0.0.1", visitor.ListenAddress);
        Assert.Equal(2222, visitor.ListenPort);
    }

    [Fact]
    public void ShorthandRequiresExplicitVisitorAndTarget()
    {
        var peer = new PeerOptions
        {
            Share = { ["ssh"] = "22@" },
            Connect = { ["ssh"] = "2222" }
        };

        Assert.Throws<InvalidOperationException>(peer.GetServices);
        Assert.Throws<InvalidOperationException>(peer.GetVisitors);
        peer.Connect.Clear();
        peer.Connect["home/ssh"] = "invalid";
        Assert.Throws<InvalidOperationException>(peer.GetVisitors);
    }

    [Fact]
    public void ShareCanOpenAPortRangeAndLeaveTheChoiceToTheVisitor()
    {
        using var config = new ConfigurationManager();
        config["FastTunnel:P2P:Share:dev"] = "3389, 8000-8100@laptop";
        config["FastTunnel:P2P:Share:any"] = "*@laptop";
        config["FastTunnel:P2P:Connect:home/dev/3389"] = "13389";
        var peer = config.GetSection("FastTunnel").Get<DefaultClientConfig>()!.P2P;

        var dev = Assert.Single(peer.GetServices(), service => service.Name == "dev");
        Assert.Equal(0, dev.LocalPort);
        Assert.Equal("3389, 8000-8100", dev.Ports);
        Assert.Equal(new[] { "laptop" }, dev.AllowVisitors);
        Assert.Equal("*", Assert.Single(peer.GetServices(), service => service.Name == "any").Ports);

        var visitor = Assert.Single(peer.GetVisitors());
        Assert.Equal("home", visitor.Provider);
        Assert.Equal("dev", visitor.Service);
        Assert.Equal(13389, visitor.ListenPort);
        Assert.Equal(3389, visitor.RemotePort);

        Assert.Throws<InvalidOperationException>(() => new PeerOptions { Share = { ["bad"] = "not-a-port@laptop" } }.GetServices());
        Assert.Throws<InvalidOperationException>(() => new PeerOptions { Share = { ["bad"] = "8100-8000@laptop" } }.GetServices());
        Assert.Throws<InvalidOperationException>(() => new PeerOptions { Share = { ["bad"] = "99999@laptop" } }.GetServices());
        Assert.Throws<InvalidOperationException>(() => new PeerOptions { Connect = { ["home/dev/99999"] = "13389" } }.GetVisitors());
        Assert.Throws<InvalidOperationException>(() => new PeerOptions { Connect = { ["home/dev/0"] = "13389" } }.GetVisitors());
    }

    [Fact]
    public void PortPolicyOnlyAdmitsPortsTheProviderShared()
    {
        var fixedService = new PeerServiceOptions { Name = "ssh", LocalHost = "127.0.0.1", LocalPort = 22 };
        // 未写 Ports 时访客说了不算，旧版客户端也只带占位端口
        Assert.True(PeerClient.TryPickPort(fixedService, PeerClient.NewTarget(3389), out var port));
        Assert.Equal(22, port);
        Assert.True(PeerClient.TryPickPort(fixedService, PeerClient.PrivateStream, out port));
        Assert.Equal(22, port);

        var ranged = new PeerServiceOptions { Name = "dev", LocalHost = "127.0.0.1", Ports = "3389,8000-8100" };
        Assert.True(PeerClient.TryPickPort(ranged, PeerClient.NewTarget(8100), out port));
        Assert.Equal(8100, port);
        Assert.False(PeerClient.TryPickPort(ranged, PeerClient.NewTarget(8101), out _));
        Assert.False(PeerClient.TryPickPort(ranged, PeerClient.NewTarget(445), out _));
        // 旧版访客端不知道要访问哪个端口，不能凭猜测放行
        Assert.False(PeerClient.TryPickPort(ranged, PeerClient.PrivateStream, out _));

        var everything = new PeerServiceOptions { Name = "lab", LocalHost = "10.0.0.5", Ports = "*" };
        Assert.True(PeerClient.TryPickPort(everything, PeerClient.NewTarget(6379), out port));
        Assert.Equal(6379, port);
        Assert.False(PeerClient.TryPickPort(everything, PeerClient.NewTarget(70000), out _));
    }

    [Fact]
    public async Task IceRestartDiscardsCandidatesFromPreviousAttempt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var provider = new PeerDirectControl(visitor: false);
        await using var attempts = provider.ReadAttemptsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        provider.Receive("1\n");
        provider.Receive("1\nOoffer");
        Assert.True(await attempts.MoveNextAsync());
        var first = attempts.Current;
        Assert.Equal("Ooffer", await first.Signals.Reader.ReadAsync(timeout.Token));

        provider.Receive("2\n");
        provider.Receive("1\nCstale");
        provider.Receive("2\nCcurrent");
        Assert.True(await attempts.MoveNextAsync());
        Assert.False(await first.Signals.Reader.WaitToReadAsync(timeout.Token));
        Assert.Equal("Ccurrent", await attempts.Current.Signals.Reader.ReadAsync(timeout.Token));
        Assert.False(attempts.Current.Signals.Reader.TryRead(out _));
    }

    [Fact]
    public async Task OnlyAuthorizedVisitorCanPairAndRevocationClosesBothSides()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var directory = new TempDirectory();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = directory.Path });
        builder.Logging.ClearProviders();
        var config = new DefaultServerConfig
        {
            EnableP2P = true,
            P2PDiscoveryPort = 0,
            P2PInviteKey = new string('A', 64),
            Clients = [
                new() { Id = "home", Token = "provider-token" },
                new() { Id = "allowed", Token = "visitor-token" },
                new() { Id = "denied", Token = "other-token" }
            ]
        };
        var customOptions = new TestOptionsMonitor<DefaultServerConfig>(config);
        var certificates = new ServerCertificateProvider(customOptions, new TestHostEnvironment(directory.Path),
            NullLogger<ServerCertificateProvider>.Instance);
        using var certificate = certificates.GetCertificate();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(certificate)));
        builder.Services.AddFastTunnelServer(builder.Configuration.GetSection("FastTunnel"));
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<DefaultServerConfig>>(customOptions);
        await using var app = builder.Build();
        app.UseFastTunnelServer();
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new UriBuilder(address) { Scheme = "wss", Path = PeerProtocol.Path }.Uri;

        using var provider = await ConnectAsync("provider-token", provider: false);
        using (var denied = await ConnectAsync("other-token", provider: true))
        {
            Assert.Null(await PeerProtocol.ReceiveTextAsync(denied, timeout.Token));
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, denied.CloseStatus);
        }

        using var visitor = await ConnectAsync("visitor-token", provider: true);
        Assert.Equal("relay", await PeerProtocol.ReceiveTextAsync(visitor, timeout.Token));
        Assert.Equal("relay", await PeerProtocol.ReceiveTextAsync(provider, timeout.Token));
        var payload = new byte[] { 1, 2, 3, 4 };
        await visitor.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        var received = new byte[payload.Length];
        var result = await provider.ReceiveAsync(received.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.EndOfMessage);
        Assert.Equal(payload, received);

        var invite = PeerInvites.Create(config.P2PInviteKey, "rdp", 1);
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(invite.ProviderToken, false,
            service: "ssh", allowedId: invite.VisitorId));
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(invite.VisitorToken, true,
            service: "rdp", providerId: "home"));
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(invite.ProviderToken, false,
            service: "rdp", allowedId: "allowed"));
        using var testHost = await ConnectAsync(invite.ProviderToken, false, service: "rdp", allowedId: invite.VisitorId);
        using var testGuest = await ConnectAsync(invite.VisitorToken, true, service: "rdp", providerId: invite.ProviderId);
        Assert.Equal("relay", await PeerProtocol.ReceiveTextAsync(testGuest, timeout.Token));
        Assert.Equal("relay", await PeerProtocol.ReceiveTextAsync(testHost, timeout.Token));

        config.EnableP2P = false;
        var closed = await Task.WhenAll(PeerProtocol.ReceiveTextAsync(provider, timeout.Token),
            PeerProtocol.ReceiveTextAsync(visitor, timeout.Token));
        Assert.All(closed, message => Assert.Null(message));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, provider.CloseStatus);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, visitor.CloseStatus);
        await app.StopAsync(timeout.Token);

        async Task<ClientWebSocket> ConnectAsync(string token, bool provider,
            string service = "ssh", string providerId = "home", string allowedId = "allowed")
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, remote, _, _) =>
                remote?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)
                == certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            socket.Options.SetRequestHeader(ProtocolConst.FASTTUNNEL_VERSION, "3.0");
            socket.Options.SetRequestHeader(ProtocolConst.FASTTUNNEL_TOKEN, token);
            socket.Options.SetRequestHeader(PeerProtocol.Service, service);
            // Mixed wire versions must still pair using the shared WSS protocol.
            socket.Options.SetRequestHeader(PeerProtocol.Direct, provider ? "2" : PeerProtocol.DirectVersion);
            if (provider)
                socket.Options.SetRequestHeader(PeerProtocol.Provider, providerId);
            else
                socket.Options.SetRequestHeader(PeerProtocol.Visitors, allowedId);
            try { await socket.ConnectAsync(uri, timeout.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
    }
}
