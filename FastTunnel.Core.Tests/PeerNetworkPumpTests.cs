using System.Net;
using System.Threading.Channels;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Mux;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// L3 组网泵的转发决策：只搬运属于本网段的 IPv4 包，且只承认组网目录里成员自己声明的地址。
/// 用内存虚拟网卡 + 一对内存 MuxSession 驱动，不需要真实网卡和管理员权限。
/// </summary>
public class PeerNetworkPumpTests
{
    private const string Laptop = "laptop";
    private const string Nas = "nas";
    private const string LaptopIp = "100.64.0.1";
    private const string NasIp = "100.64.0.2";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private static PeerNetworkRoster Roster() => new()
    {
        Cidr = "100.64.0.0/16",
        Self = Laptop,
        Peers =
        [
            new PeerNetworkEntry { Id = Laptop, Name = "笔记本", Ip = LaptopIp },
            new PeerNetworkEntry { Id = Nas, Name = "群晖", Ip = NasIp },
        ],
    };

    [Fact]
    public void 目录给出本端地址与其余成员()
    {
        using var fixture = new PumpFixture();

        Assert.Equal(LaptopIp, fixture.Network.VirtualIp);
        Assert.Equal(new[] { Nas }, fixture.Network.Peers);
        Assert.Equal(PeerProtocol.PrivateId, fixture.Network.NewTarget().Id);
        Assert.Equal("ip/" + LaptopIp, fixture.Network.NewTarget().Host);
        Assert.False(fixture.Network.HasChannel(Nas));
    }

    [Fact]
    public void 只接受目录内成员的地址声明()
    {
        using var fixture = new PumpFixture();

        Assert.True(fixture.Network.TryAdmit(Target(NasIp), out var peerId));
        Assert.Equal(Nas, peerId);

        // 前缀不对、地址不在目录里、谎报本端地址、地址不在网段里，都不能认领通道
        Assert.False(fixture.Network.TryAdmit(new SwapRequest { Id = PeerProtocol.PrivateId, Host = "8080", Port = 1 }, out _));
        Assert.False(fixture.Network.TryAdmit(Target("100.64.0.9"), out _));
        Assert.False(fixture.Network.TryAdmit(Target(LaptopIp), out _));
        Assert.False(fixture.Network.TryAdmit(Target("10.0.0.5"), out _));
    }

    [Fact]
    public async Task 发往成员地址的包走它的通道()
    {
        await using var fixture = new PumpFixture();
        await fixture.ConnectAsync();
        var packet = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, LaptopIp, NasIp, [0x04, 0xB0, 0x01, 0xBB]);

        await fixture.Tun.SendFromKernelAsync(packet);

        Assert.Equal(packet, await fixture.ReceiveFromPeerAsync(Timeout));
    }

    [Fact]
    public async Task 通道未就绪时静默丢包不影响后续转发()
    {
        await using var fixture = new PumpFixture();
        await fixture.Tun.SendFromKernelAsync(
            TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, LaptopIp, NasIp, [0x04, 0xB0, 0x01, 0xBB]));

        Assert.False(fixture.Network.HasChannel(Nas));
        await Task.Delay(Quiet);
        Assert.False(fixture.PumpRun.IsCompleted);

        await fixture.ConnectAsync();
        var packet = TestPackets.Ipv4(PeerIpPacket.ProtocolUdp, LaptopIp, NasIp, [0x04, 0xB0, 0x00, 0x35]);
        await fixture.Tun.SendFromKernelAsync(packet);

        Assert.Equal(packet, await fixture.ReceiveFromPeerAsync(Timeout));
    }

    [Fact]
    public async Task 成员送来的包交回网卡()
    {
        await using var fixture = new PumpFixture();
        await fixture.ConnectAsync();
        var packet = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, NasIp, LaptopIp, [0x01, 0xBB, 0x04, 0xB0]);

        await fixture.SendFromPeerAsync(packet);

        Assert.Equal(packet, await fixture.Tun.ReceiveFromNetworkAsync(Timeout));
    }

    [Fact]
    public async Task 丢弃源地址冒充他人或目的地越界的包()
    {
        await using var fixture = new PumpFixture();
        await fixture.ConnectAsync();

        // 源地址不是本通道成员（哪怕它在自己网段里），或目的地根本不在网段内
        await fixture.SendFromPeerAsync(TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, "100.64.0.9", LaptopIp, [0x01, 0xBB, 0x04, 0xB0]));
        await fixture.SendFromPeerAsync(TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, NasIp, "8.8.8.8", [0x01, 0xBB, 0x04, 0xB0]));

        Assert.Null(await fixture.Tun.ReceiveFromNetworkAsync(Quiet));
        Assert.False(fixture.PumpRun.IsCompleted);
    }

    [Fact]
    public async Task 非网段地址与自己地址不投递给成员()
    {
        await using var fixture = new PumpFixture();
        await fixture.ConnectAsync();

        await fixture.Tun.SendFromKernelAsync(TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, LaptopIp, "8.8.8.8", [0x04, 0xB0, 0x00, 0x50]));
        await fixture.Tun.SendFromKernelAsync(TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, LaptopIp, LaptopIp, [0x04, 0xB0, 0x00, 0x50]));
        await fixture.Tun.SendFromKernelAsync([0x60, 0x00, 0x00, 0x00]);

        Assert.Null(await fixture.ReceiveFromPeerAsync(Quiet));
        Assert.False(fixture.PumpRun.IsCompleted);
    }

    [Fact]
    public async Task 同一成员重复建通道只保留先到的一条()
    {
        await using var fixture = new PumpFixture();
        await fixture.ConnectAsync();
        var duplicate = (await fixture.OpenAsync(NasIp)).Local;

        Assert.True(await PumpFixture.WaitUntilAsync(() => duplicate.IsDisposed));
        Assert.False(fixture.First!.IsDisposed);
        Assert.True(fixture.Network.HasChannel(Nas));

        var packet = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, LaptopIp, NasIp, [0x04, 0xB0, 0x01, 0xBB]);
        await fixture.Tun.SendFromKernelAsync(packet);

        Assert.Equal(packet, await fixture.ReceiveFromPeerAsync(Timeout));
    }

    [Fact]
    public async Task 谎报目录外地址的通道直接关闭()
    {
        await using var fixture = new PumpFixture();
        var stranger = (await fixture.OpenAsync("100.64.0.77")).Local;

        Assert.True(await PumpFixture.WaitUntilAsync(() => stranger.IsDisposed));
        Assert.False(fixture.Network.HasChannel(Nas));
    }

    private static SwapRequest Target(string ip)
        => new() { Id = PeerProtocol.PrivateId, Host = PeerProtocol.IpService + "/" + ip, Port = 1 };

    [Fact]
    public void 每对成员只由编号较小的一侧拨组网速流()
    {
        Assert.True(PeerClient.IsDialer(Laptop, Nas));
        Assert.False(PeerClient.IsDialer(Nas, Laptop));
        // 大小写不敏感：服务端把主体编号统一成大写比较，两侧不能因为写法不同都去拨号
        Assert.True(PeerClient.IsDialer("NAS", "nas1"));
        Assert.False(PeerClient.IsDialer("nas1", "NAS"));
    }

    /// <summary>
    /// 用 PeerClient 的真实接线跑一对成员：拨号方开完流也要把同一条流交给自己的泵，
    /// 被叫方靠 CreateMux 的接受回调按声明地址认领，这样两侧的通道键才一致、双向都通。
    /// </summary>
    [Fact]
    public async Task 两端各自的生产接线把包送到对方网卡()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (dialerPipe, answerPipe) = TestPipes.CreatePair();
        await using var dialerTun = new FakeTun();
        await using var answerTun = new FakeTun();
        await using var dialerSide = new PeerNetwork(Roster(), dialerTun, NullLogger.Instance);
        await using var answerSide = new PeerNetwork(NasRoster(), answerTun, NullLogger.Instance);
        var dialer = PeerClient.CreateMux(dialerPipe, null, null, NullLogger.Instance);
        var answer = PeerClient.CreateMux(answerPipe, null, answerSide, NullLogger.Instance);
        var loops = new[] { dialer.RunAsync(), answer.RunAsync(),
            dialerSide.RunAsync(cts.Token), answerSide.RunAsync(cts.Token) };
        try
        {
            var stream = await dialer.OpenStreamAsync(dialerSide.NewTarget(), cts.Token);
            _ = dialerSide.ServeAsync(Nas, stream, cts.Token);
            Assert.True(await PumpFixture.WaitUntilAsync(() => answerSide.HasChannel(Laptop) && dialerSide.HasChannel(Nas)));

            var outbound = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, LaptopIp, NasIp, [0x04, 0xB0, 0x01, 0xBB]);
            await dialerTun.SendFromKernelAsync(outbound);
            Assert.Equal(outbound, await answerTun.ReceiveFromNetworkAsync(Timeout));

            var inbound = TestPackets.Ipv4(PeerIpPacket.ProtocolTcp, NasIp, LaptopIp, [0x01, 0xBB, 0x04, 0xB0]);
            await answerTun.SendFromKernelAsync(inbound);
            Assert.Equal(inbound, await dialerTun.ReceiveFromNetworkAsync(Timeout));
        }
        finally
        {
            // 会话不先关掉，搬运循环就永远等在管道上
            await dialer.DisposeAsync();
            await answer.DisposeAsync();
            cts.Cancel();
            await Task.WhenAny(Task.WhenAll(loops), Task.Delay(Timeout));
        }
    }

    /// <summary>被叫方不接受目录外的地址声明，也不会因此影响会话本身。</summary>
    [Fact]
    public async Task 接线拒绝目录外的地址声明()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (dialerPipe, answerPipe) = TestPipes.CreatePair();
        await using var dialerTun = new FakeTun();
        await using var answerTun = new FakeTun();
        await using var dialerSide = new PeerNetwork(Roster(), dialerTun, NullLogger.Instance);
        await using var answerSide = new PeerNetwork(NasRoster(), answerTun, NullLogger.Instance);
        var dialer = PeerClient.CreateMux(dialerPipe, null, null, NullLogger.Instance);
        var answer = PeerClient.CreateMux(answerPipe, null, answerSide, NullLogger.Instance);
        var loops = new[] { dialer.RunAsync(), answer.RunAsync(),
            dialerSide.RunAsync(cts.Token), answerSide.RunAsync(cts.Token) };
        try
        {
            // 地址不在目录里：不认通道，也不能把整条会话带崩
            _ = await dialer.OpenStreamAsync(Target("100.64.0.9"), cts.Token);
            await Task.Delay(Quiet);
            Assert.False(answerSide.HasChannel(Laptop) || answerSide.HasChannel(Nas));

            var stream = await dialer.OpenStreamAsync(dialerSide.NewTarget(), cts.Token);
            _ = dialerSide.ServeAsync(Nas, stream, cts.Token);
            Assert.True(await PumpFixture.WaitUntilAsync(() => answerSide.HasChannel(Laptop) && dialerSide.HasChannel(Nas)));
        }
        finally
        {
            await dialer.DisposeAsync();
            await answer.DisposeAsync();
            cts.Cancel();
            await Task.WhenAny(Task.WhenAll(loops), Task.Delay(Timeout));
        }
    }

    /// <summary>同一对成员里 nas 视角的目录：网段和成员不变，只是「自己」换了。</summary>
    private static PeerNetworkRoster NasRoster()
    {
        var roster = Roster();
        roster.Self = Nas;
        return roster;
    }

    /// <summary>
    /// 客户端从真实服务端领目录：开关、指纹、入网台账任一处不满足都只留日志，不抛异常。
    /// </summary>
    public class DirectoryFetch
    {
        [Fact]
        public async Task 入网设备领到本网段目录()
        {
            using var server = await PeerNetworkTests.RosterServer.StartAsync(enableL3: true);
            server.Enroll("laptop", server.Config.P2PInviteKey);

            using var directory = new PeerNetworkDirectory(server.Node(), "device-token", NullLogger.Instance);
            var roster = await directory.FetchAsync(default);

            Assert.NotNull(roster);
            Assert.Equal("100.64.0.0/16", roster!.Cidr);
            Assert.Equal(Laptop, roster.Self);
            Assert.Equal(LaptopIp, Assert.Single(roster.Peers).Ip);
        }

        [Fact]
        public async Task 服务端未开启或凭据无效时只留日志()
        {
            using var off = await PeerNetworkTests.RosterServer.StartAsync(enableL3: false);
            using (var disabled = new PeerNetworkDirectory(off.Node(), "device-token", NullLogger.Instance))
            {
                Assert.Null(await disabled.FetchAsync(default));
            }

            using var server = await PeerNetworkTests.RosterServer.StartAsync(enableL3: true);
            using (var outsider = new PeerNetworkDirectory(server.Node(), "other-token", NullLogger.Instance))
            {
                Assert.Null(await outsider.FetchAsync(default));
            }

            using (var anonymous = new PeerNetworkDirectory(server.Node(), "", NullLogger.Instance))
            {
                Assert.Null(await anonymous.FetchAsync(default));
            }
        }

        [Fact]
        public async Task 指纹不匹配时拒绝领取()
        {
            using var server = await PeerNetworkTests.RosterServer.StartAsync(enableL3: true);
            server.Enroll("laptop", server.Config.P2PInviteKey);
            var node = server.Node();
            node.CertificateFingerprint = new string('F', 64);

            using var directory = new PeerNetworkDirectory(node, "device-token", NullLogger.Instance);

            Assert.Null(await directory.FetchAsync(default));
        }

        [Fact]
        public async Task 网段配置无效时不返回半截目录()
        {
            using var server = await PeerNetworkTests.RosterServer.StartAsync(enableL3: true, cidr: "100.64.0.0/33");
            server.Enroll("laptop", server.Config.P2PInviteKey);

            using var directory = new PeerNetworkDirectory(server.Node(), "device-token", NullLogger.Instance);

            Assert.Null(await directory.FetchAsync(default));
        }
    }

    /// <summary>从对端那条流里取一条数据报；超时或流结束返回 null。</summary>
    private static async Task<byte[]?> ReadDatagramAsync(MuxStream stream, TimeSpan limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var scratch = new byte[UdpDatagramFramer.MaxDatagramSize];
        try
        {
            while (true)
            {
                var result = await stream.Input.ReadAsync(timeout.Token);
                var buffer = result.Buffer;
                if (UdpDatagramFramer.TryRead(buffer, scratch, out var payload, out var total))
                {
                    stream.Input.AdvanceTo(buffer.GetPosition(total));
                    return payload.ToArray();
                }

                stream.Input.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>内存虚拟网卡：一边是「内核发给网卡的包」，一边是「泵交给内核的包」。</summary>
    private sealed class FakeTun : ITunDevice
    {
        private readonly Channel<byte[]> fromKernel = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> toKernel = Channel.CreateUnbounded<byte[]>();

        public string Name => "ft-test";

        internal Task SendFromKernelAsync(byte[] packet)
        {
            fromKernel.Writer.TryWrite(packet);
            return Task.CompletedTask;
        }

        internal async Task<byte[]?> ReceiveFromNetworkAsync(TimeSpan limit)
        {
            using var timeout = new CancellationTokenSource(limit);
            try
            {
                return await toKernel.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (!await fromKernel.Reader.WaitToReadAsync(token))
            {
                return 0;
            }

            var packet = await fromKernel.Reader.ReadAsync(token);
            packet.CopyTo(buffer);
            return packet.Length;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
            => await toKernel.Writer.WriteAsync(packet.ToArray(), token);

        public ValueTask DisposeAsync()
        {
            fromKernel.Writer.TryComplete();
            toKernel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 虚拟网卡 + 组网泵 + 与生产接线一致的接受路径：先按 SYN 声明的地址认领成员，再交给泵。
    /// 每条被接受的流都留在 <see cref="Local"/> 里，方便断言「哪条被关掉了」。
    /// </summary>
    private sealed class PumpFixture : IDisposable, IAsyncDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private readonly TestMuxPair pair;
        private MuxStream? peer;

        internal PumpFixture()
        {
            Network = new PeerNetwork(Roster(), Tun, NullLogger.Instance);
            pair = new TestMuxPair((stream, request) =>
            {
                Local.Add(stream);
                if (Network.TryAdmit(request, out var peerId))
                {
                    _ = Network.ServeAsync(peerId, stream, cts.Token);
                }
                else
                {
                    _ = stream.DisposeAsync();
                }

                return Task.CompletedTask;
            });
            PumpRun = Network.RunAsync(cts.Token);
            PumpRun.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        internal FakeTun Tun { get; } = new();

        internal PeerNetwork Network { get; }

        internal Task PumpRun { get; }

        internal List<MuxStream> Local { get; } = [];

        /// <summary>第一条被接受的流，即通道实际使用的那条。</summary>
        internal MuxStream? First => Local.Count > 0 ? Local[0] : null;

        /// <summary>成员主动连进来，建立一条可用通道。</summary>
        internal async Task ConnectAsync()
        {
            peer = (await OpenAsync(NasIp)).Remote;
            Assert.True(await WaitUntilAsync(() => Network.HasChannel(Nas)));
        }

        /// <summary>对端开一条流，返回发起方与本端两侧的火把。</summary>
        internal async Task<(MuxStream Remote, MuxStream Local)> OpenAsync(string announcedIp)
        {
            var index = Local.Count;
            var remote = await pair.Server.OpenStreamAsync(Target(announcedIp), cts.Token);
            Assert.True(await WaitUntilAsync(() => Local.Count > index), $"本端未处理 {announcedIp} 的连接");
            return (remote, Local[index]);
        }

        internal Task<byte[]?> ReceiveFromPeerAsync(TimeSpan limit)
            => peer == null
                ? throw new InvalidOperationException("尚未建立通道")
                : ReadDatagramAsync(peer, limit, cts.Token);

        internal Task SendFromPeerAsync(byte[] packet)
            => peer == null
                ? throw new InvalidOperationException("尚未建立通道")
                : peer.WriteDatagramAsync(packet, cts.Token).AsTask();

        internal static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await Network.DisposeAsync();
            if (peer != null)
            {
                await peer.DisposeAsync();
            }

            await pair.DisposeAsync();
            await Task.WhenAny(PumpRun, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }
}
