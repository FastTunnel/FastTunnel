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

        config.EnableP2P = false;
        var closed = await Task.WhenAll(PeerProtocol.ReceiveTextAsync(provider, timeout.Token),
            PeerProtocol.ReceiveTextAsync(visitor, timeout.Token));
        Assert.All(closed, message => Assert.Null(message));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, provider.CloseStatus);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, visitor.CloseStatus);
        await app.StopAsync(timeout.Token);

        async Task<ClientWebSocket> ConnectAsync(string token, bool provider)
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, remote, _, _) =>
                remote?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)
                == certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            socket.Options.SetRequestHeader(ProtocolConst.FASTTUNNEL_VERSION, "3.0");
            socket.Options.SetRequestHeader(ProtocolConst.FASTTUNNEL_TOKEN, token);
            socket.Options.SetRequestHeader(PeerProtocol.Service, "ssh");
            // Mixed wire versions must still pair using the shared WSS protocol.
            socket.Options.SetRequestHeader(PeerProtocol.Direct, provider ? "2" : PeerProtocol.DirectVersion);
            if (provider)
                socket.Options.SetRequestHeader(PeerProtocol.Provider, "home");
            else
                socket.Options.SetRequestHeader(PeerProtocol.Visitors, "allowed");
            try { await socket.ConnectAsync(uri, timeout.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
    }
}
