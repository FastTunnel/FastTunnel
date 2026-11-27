using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 组网 DNS 应答器：只承认组网目录里的成员，其余查询转给上游；
/// 应答以本端虚拟地址的 53 端口收发，所以查询根本不会出现在网卡上。
/// </summary>
public class PeerDnsTests
{
    private const string LaptopIp = "100.64.0.1";
    private const string NasIp = "100.64.0.2";
    private const ushort QueryId = 0x4F2A;   // 与 TestDns.Query 的默认事务编号一致

    [Fact]
    public async Task 成员编号解析成它的虚拟地址()
    {
        var answer = await AskAsync(Roster(), "nas.ft");

        Assert.Equal(0, answer.Rcode);
        Assert.Equal(NasIp, Assert.Single(answer.Addresses));
        // 应用只按事务编号配对，答案必须带着它问的那个编号回来
        Assert.Equal(QueryId, answer.Id);
    }

    [Fact]
    public async Task 纯ASCII成员名也认中文名不认()
    {
        var roster = Roster();
        roster.Peers[1].Name = "DiskStation";
        Assert.Equal(NasIp, Assert.Single((await AskAsync(roster, "diskstation.ft")).Addresses));
        // 中文名要靠 punycode，这里不猜写法，直接当作不存在
        Assert.Equal(3, (await AskAsync(Roster(), "群晖.ft")).Rcode);
    }

    [Fact]
    public async Task 大写与末尾的点不影响名字解析()
        => Assert.Equal(NasIp, Assert.Single((await AskAsync(Roster(), "NAS.FT.")).Addresses));

    [Fact]
    public async Task 目录外的成员名回NXDOMAIN且AAAA查询回空答案()
    {
        Assert.Equal(3, (await AskAsync(Roster(), "ghost.ft")).Rcode);
        var answer = await AskAsync(Roster(), "nas.ft", qType: 28);
        Assert.Equal(0, answer.Rcode);
        Assert.Empty(answer.Addresses);
    }

    [Fact]
    public async Task 组网之外的域名转给上游并原样带回()
    {
        var upstream = EchoServer();

        var answer = await ReadAsync((await ResolveAsync(Roster(), TestDns.Query("www.example.com."),
            [upstream]))!);

        Assert.Equal(0, answer.Rcode);
        Assert.Equal("93.184.216.34", Assert.Single(answer.Addresses));
        Assert.Equal(QueryId, answer.Id);
    }

    [Fact]
    public async Task 上游没有回话时沉默不给应用假答案()
        => Assert.Null(await ResolveAsync(Roster(), TestDns.Query("www.example.com."), [UnusedEndpoint()]));

    [Fact]
    public void 只有可单播且不在组网里的地址才当上游()
    {
        var cidr = PeerNetworkCidr.Parse("100.64.0.0/16")!;
        var servers = PeerDns.Filter([
            IPAddress.Loopback,                                  // 自己
            IPAddress.Parse("100.64.0.9"),                       // 组网里的成员，转过去等于自己问自己
            IPAddress.Parse("224.0.0.251"),                      // 组播
            IPAddress.IPv6Any,                                   // 本网段只有 IPv4
            IPAddress.Parse("223.5.5.5"),
            IPAddress.Parse("8.8.8.8"),
        ], cidr);

        Assert.Equal(["223.5.5.5:53", "8.8.8.8:53"], servers.Select(x => x.ToString()).ToArray());
    }

    [Fact]
    public async Task 没有可用上游时沉默()
        => Assert.Null(await ResolveAsync(Roster(), TestDns.Query("www.example.com."), []));

    [Fact]
    public async Task 不是标准查询就不应答()
    {
        // 响应报文、多问题、带压缩指针的查询都不接
        Assert.Null(await ResolveAsync(Roster(), TestDns.Query("nas.ft", response: true)));
        Assert.Null(await ResolveAsync(Roster(), [0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0, 0, 0, 0]));
        Assert.Null(await ResolveAsync(Roster(), [0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0xC0, 0x0C, 0, 1, 0, 1]));
        Assert.Null(await ResolveAsync(Roster(), [0x00, 0x01, 0x01, 0x00]));
    }

    [Fact]
    public async Task 超过上限的查询拒答而不是崩溃()
    {
        var huge = new byte[5000];
        Array.Copy(TestDns.Query("nas.ft"), huge, 12);

        var answer = await ReadAsync((await ResolveAsync(Roster(), huge))!);
        Assert.Equal(5, answer.Rcode);
        // 问题段没回显，就不能谎称带了一个问题
        Assert.Equal(0, answer.Questions);
    }

    [Fact]
    public async Task 非IN类别的查询拒答()
    {
        var answer = await ReadAsync((await ResolveAsync(Roster(), TestDns.Query("nas.ft", qClass: 255)))!);
        Assert.Equal(5, answer.Rcode);
    }

    [Fact]
    public async Task 占住端口后应用一问本端就一答()
    {
        // 测试机上没有 100.64.0.1 这块网卡，退到回环的临时端口跑同一套收发
        await using var dns = PeerDns.Start(Roster(), "127.0.0.1", NullLogger.Instance, port: 0);
        Assert.NotNull(dns);
        using var app = new UdpClient(AddressFamily.InterNetwork);
        var server = (IPEndPoint)dns!.LocalEndPoint!;

        await app.SendAsync(TestDns.Query("nas.ft"), server);
        var reply = await app.ReceiveAsync();

        var answer = await ReadAsync(reply.Buffer);
        Assert.Equal(NasIp, Assert.Single(answer.Addresses));
    }

    [Fact]
    public async Task 本端地址配不上时退回不接管解析()
    {
        // 网卡还没拿到地址、或 53 被别的服务占了，都只是不再提供域名功能
        Assert.Null(PeerDns.Start(Roster(), "100.64.0.1", NullLogger.Instance));
        Assert.Null(PeerDns.Start(Roster(), "不是地址", NullLogger.Instance));
    }

    private static PeerNetworkRoster Roster() => new()
    {
        Cidr = "100.64.0.0/16",
        Self = "laptop",
        Peers =
        [
            new PeerNetworkEntry { Id = "laptop", Name = "笔记本", Ip = LaptopIp },
            new PeerNetworkEntry { Id = "nas", Name = "群晖", Ip = NasIp },
        ],
    };

    private static async Task<Answer> AskAsync(PeerNetworkRoster roster, string name, ushort qType = 1)
        => await ReadAsync((await ResolveAsync(roster, TestDns.Query(name, qType: qType)))!);

    private static async Task<byte[]?> ResolveAsync(PeerNetworkRoster roster, byte[] query, IPEndPoint[]? upstream = null)
    {
        await using var dns = new PeerDns(roster, NullLogger.Instance, upstream);
        return await dns.ResolveAsync(query, default);
    }

    private static async Task<Answer> ReadAsync(byte[] wire)
    {
        var span = wire.AsSpan();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        var records = new List<string>();
        var at = 12;
        // 问题段：标签一路读到 0 长度，再跳过类型和类别；被拒答的超长查询没有问题段
        if (BinaryPrimitives.ReadUInt16BigEndian(span[4..]) != 0)
        {
            while (span[at] != 0)
            {
                at += 1 + span[at];
            }

            at += 5;
        }

        var answers = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        for (var index = 0; index < answers; index++)
        {
            Assert.Equal(0xC0, span[at]);
            Assert.Equal(0x0C, span[at + 1]);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(span[(at + 2)..]));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(span[(at + 4)..]));  // class IN
            Assert.Equal(60u, BinaryPrimitives.ReadUInt32BigEndian(span[(at + 6)..])); // ttl
            Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(span[(at + 10)..]));
            records.Add(new IPAddress(span[(at + 12)..(at + 16)].ToArray()).ToString());
            at += 16;
        }

        return new Answer(BinaryPrimitives.ReadUInt16BigEndian(span[..2]), (byte)(flags & 0xF),
            BinaryPrimitives.ReadUInt16BigEndian(span[4..]), [.. records]);
    }

    /// <summary>假上游：收到任何查询都回一条固定的 A 记录。</summary>
    private static IPEndPoint EchoServer()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var buffer = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        new Thread(() =>
        {
            while (true)
            {
                int length;
                try
                {
                    length = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (Exception)
                {
                    return;
                }

                if (length <= 12)
                {
                    continue;
                }

                var reply = new byte[length + 16];
                Array.Copy(buffer, reply, length);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), 0x8180);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6, 2), 1);
                var at = length;
                reply[at++] = 0xC0;
                reply[at++] = 0x0C;
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(at, 2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(at + 2, 2), 1);
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(at + 4, 4), 60);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(at + 8, 2), 4);
                IPAddress.Parse("93.184.216.34").GetAddressBytes().CopyTo(reply, at + 10);
                try
                {
                    socket.SendTo(reply, remote);
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        { IsBackground = true }.Start();
        return (IPEndPoint)socket.LocalEndPoint!;
    }

    private static IPEndPoint UnusedEndpoint()
    {
        var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)probe.LocalEndPoint!;
        probe.Dispose();
        return endpoint;
    }

    private sealed record Answer(ushort Id, byte Rcode, ushort Questions, string[] Addresses);
}
