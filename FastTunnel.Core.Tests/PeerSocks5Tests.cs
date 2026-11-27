using System.Net;
using System.Net.Sockets;
using System.Text;
using FastTunnel.Core.Config;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>访客端 SOCKS5 出口：出口名单、主机名选路，以及握手只吃自己的字节。</summary>
public class PeerSocks5Tests
{
    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [Fact]
    public void ExitsAreProviderServicePairsAndRejectAmbiguity()
    {
        Assert.Equal(new[] { new PeerSocks5.Exit("home", "dev") }, PeerSocks5.ParseExits(["home/dev"]));
        Assert.Throws<InvalidOperationException>(() => PeerSocks5.ParseExits(["home"]));
        Assert.Throws<InvalidOperationException>(() => PeerSocks5.ParseExits(["home/dev/3389"]));
        Assert.Throws<InvalidOperationException>(() => PeerSocks5.ParseExits(["home/bad name"]));
        Assert.Throws<InvalidOperationException>(() => PeerSocks5.ParseExits(["home/dev", "home/dev"]));
        Assert.Throws<InvalidOperationException>(() => PeerSocks5.ParseExits([]));
        Assert.Throws<InvalidOperationException>(() => PeerSocks5.ParseExits(
            Enumerable.Range(0, PeerSocks5.MaxExits + 1).Select(index => $"p{index}/s")));
    }

    [Fact]
    public void ExitsFallBackToTheConfiguredVisitors()
    {
        using var config = new ConfigurationManager();
        config["FastTunnel:P2P:Socks5"] = "1080";
        config["FastTunnel:P2P:Connect:home/dev/3389"] = "13389";
        config["FastTunnel:P2P:Connect:lab/ssh"] = "2222";
        var peer = config.GetSection("FastTunnel").Get<DefaultClientConfig>()!.P2P;

        Assert.True(peer.HasEndpoints);
        Assert.Equal(1080, peer.Socks5);
        Assert.Equal(
            [new PeerSocks5.Exit("home", "dev"), new PeerSocks5.Exit("lab", "ssh")],
            PeerSocks5.ResolveExits(peer, peer.GetVisitors()));

        config["FastTunnel:P2P:Socks5Via:0"] = "home/db";
        var scoped = config.GetSection("FastTunnel").Get<DefaultClientConfig>()!.P2P;
        Assert.Equal([new PeerSocks5.Exit("home", "db")], PeerSocks5.ResolveExits(scoped, scoped.GetVisitors()));
    }

    [Fact]
    public void HostnameSelectsTheExitAndPlainNamesNeedASingleExit()
    {
        var exits = PeerSocks5.ParseExits(["home/dev", "home/db", "lab/ssh"]);

        Assert.True(PeerSocks5.TryResolve("dev.home.ft", exits, out var index));
        Assert.Equal(0, index);
        Assert.True(PeerSocks5.TryResolve("DB.HOME.FT", exits, out index));
        Assert.Equal(1, index);
        Assert.True(PeerSocks5.TryResolve("ssh.lab.ft", exits, out index));
        Assert.Equal(2, index);
        // 只写提供端时，多个服务之间有歧义就不能猜
        Assert.False(PeerSocks5.TryResolve("home.ft", exits, out _));
        Assert.True(PeerSocks5.TryResolve("lab.ft", exits, out index));
        Assert.Equal(2, index);
        Assert.False(PeerSocks5.TryResolve("nas.home.local", exits, out _));
        Assert.False(PeerSocks5.TryResolve("127.0.0.1", exits, out _));
        Assert.False(PeerSocks5.TryResolve(".ft", exits, out _));
        Assert.False(PeerSocks5.TryResolve("unknown.home.ft", exits, out _));

        var only = PeerSocks5.ParseExits(["home/dev"]);
        Assert.True(PeerSocks5.TryResolve("intranet.example.com", only, out index));
        Assert.Equal(0, index);
        Assert.True(PeerSocks5.TryResolve("10.0.0.5", only, out index));
        Assert.Equal(0, index);
    }

    [Fact]
    public async Task HandshakeParsesTheTargetWithoutTouchingPayload()
    {
        var (client, server) = await SocksProbe.PairAsync(TestToken());
        using (client)
        using (server)
        {
            await client.SendAsync(SocksProbe.Greeting().Concat(SocksProbe.Request("dev.home.ft", 3389)).ToArray(),
                SocketFlags.None, TestToken());

            var (host, port) = await PeerSocks5.ReadRequestAsync(server, TestToken());
            Assert.Equal("dev.home.ft", host);
            Assert.Equal(3389, port);

            await PeerSocks5.WriteSuccessAsync(server, TestToken());
            var reply = await SocksProbe.ReadExactAsync(client, 12, TestToken());
            Assert.Equal(new byte[] { 5, 0, 5, 0, 0 }, reply[..5]);
            Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0 }, reply[5..]);

            // 握手之后的字节必须原样留给数据面
            await client.SendAsync(new byte[] { 42, 43 }, SocketFlags.None, TestToken());
            Assert.Equal(new byte[] { 42, 43 }, await SocksProbe.ReadExactAsync(server, 2, TestToken()));
        }
    }

    [Fact]
    public async Task HandshakeRejectsOtherCommands()
    {
        var (client, server) = await SocksProbe.PairAsync(TestToken());
        using (client)
        using (server)
        {
            await client.SendAsync(SocksProbe.Greeting().Concat(SocksProbe.Request("dev.home.ft", 22, command: 2)).ToArray(),
                SocketFlags.None, TestToken());
            await Assert.ThrowsAsync<IOException>(() => PeerSocks5.ReadRequestAsync(server, TestToken()));
            Assert.Equal(new byte[] { 5, 0 }, await SocksProbe.ReadExactAsync(client, 2, TestToken()));
            Assert.Equal(7, (await SocksProbe.ReadExactAsync(client, 10, TestToken()))[1]);
        }
    }

    [Fact]
    public async Task HandshakeAcceptsAddressTargets()
    {
        var (client, server) = await SocksProbe.PairAsync(TestToken());
        using (client)
        using (server)
        {
            await client.SendAsync(SocksProbe.Greeting()
                .Concat(SocksProbe.Request("dev.home.ft", 22, atyp: 1, address: "192.0.2.7")).ToArray(),
                SocketFlags.None, TestToken());
            var (host, port) = await PeerSocks5.ReadRequestAsync(server, TestToken());
            Assert.Equal("192.0.2.7", host);
            Assert.Equal(22, port);
        }
    }
}
