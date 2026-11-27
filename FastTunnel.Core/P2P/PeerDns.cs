using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 组网内的名字解析。虚拟网卡的 DNS 指向本端虚拟地址，所以应用查 <c>x.ft</c> 时会向它发 UDP/53。
/// 目的是本机自己地址的包由内核就地投递、不会出现在网卡上，所以这里占住该地址的 53 端口就地应答。
/// 不属于 <c>.ft</c> 的查询转给系统原本在用的 DNS，避免把用户正常上网的解析接管死；
/// 转发走临时端口的独立套接字，否则答案会被自己再收一遍。
/// </summary>
internal sealed class PeerDns(PeerNetworkRoster roster, ILogger logger, IPEndPoint[]? upstream = null) : IAsyncDisposable
{
    internal const int DnsPort = 53;

    /// <summary>EDNS 的大请求不接，超过这个长度的查询直接拒答。</summary>
    private const int MaxQuerySize = 4096;

    /// <summary>成员的虚拟地址可能被服务端重新分配，缓存别太久。</summary>
    private const uint TtlSeconds = 60;

    /// <summary>同时在途的查询数，超出的直接不理，防本机应用把组网 DNS 刷屏。</summary>
    private const int MaxInFlight = 32;

    private const byte ClassIn = 1;
    private const byte NoError = 0;
    private const byte Refused = 5;
    private const byte NxDomain = 3;
    private const ushort TypeA = 1;

    /// <summary>转发等不到答案就换下一个上游；上游全无时宁可让应用自己去超时。</summary>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);

    private readonly PeerNetworkCidr cidr = PeerNetworkCidr.Parse(roster.Cidr)!;
    private readonly Dictionary<string, string> byName = Index(roster);
    private readonly SemaphoreSlim slots = new(MaxInFlight, MaxInFlight);
    private readonly CancellationTokenSource closing = new();
    private UdpClient? listener;
    private IPEndPoint[]? forwarders;

    /// <summary>
    /// 占住本端虚拟地址的 53 端口并开始应答。占不到就退回「不接管解析」：
    /// 组网按 IP 照样连通，没必要为一个域名功能让整条 L3 起不来。
    /// </summary>
    internal static PeerDns? Start(PeerNetworkRoster roster, string selfIp, ILogger logger, int port = DnsPort)
    {
        var dns = new PeerDns(roster, logger);
        try
        {
            dns.listener = new UdpClient(new IPEndPoint(IPAddress.Parse(selfIp), port));
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or FormatException)
        {
            logger.LogWarning("无法接管 {Address}:{Port} 的解析：{Message}；把 FastTunnel:P2P:L3Dns 设为 false 可不再尝试",
                selfIp, DnsPort, ex.Message);
            return null;
        }

        _ = dns.ListenAsync();
        logger.LogInformation("组网域名解析已生效：查 *{Suffix} 走 {Endpoint}", PeerSocks5.Suffix, dns.LocalEndPoint);
        return dns;
    }

    /// <summary>实际绑上的地址，供日志和测试确认解析器真的占住了端口。</summary>
    internal EndPoint? LocalEndPoint => listener?.Client.LocalEndPoint;

    /// <summary>查询交给解析逻辑，答案按原路发回（内核直投本机地址，不经网卡）。</summary>
    private async Task ListenAsync()
    {
        var socket = listener!;
        try
        {
            while (true)
            {
                var query = await socket.ReceiveAsync(closing.Token);
                // 转发要等上游回包，逐条放到后台处理，否则一个卡住的上游能把整块解析堵死；
                // 在途已满就不理这条查询，让应用去超时，别把本机刷屏的进程喂饱
                if (await slots.WaitAsync(0, closing.Token))
                {
                    _ = AnswerAsync(socket, query.RemoteEndPoint, query.Buffer, closing.Token);
                }
            }
        }
        catch (Exception ex)
        {
            // 关闭时挂起的接收会抛取消或已释放，都不算故障
            logger.LogDebug("组网 DNS 停止接收：{Message}", ex.Message);
        }
    }

    private async Task AnswerAsync(UdpClient socket, IPEndPoint client, byte[] query, CancellationToken token)
    {
        try
        {
            if (await ResolveAsync(query, token) is { } answer)
            {
                await socket.SendAsync(answer, client, token);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("组网 DNS 应答失败：{Message}", ex.Message);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>处理一条 DNS 查询，返回要发回的报文；不是标准查询、或要转发却无处可转时返回 null。</summary>
    internal async Task<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken token)
    {
        if (query.Length is < 12 or > MaxQuerySize)
        {
            return query.Length > MaxQuerySize ? Answer(QueryId(query), [], Refused, []) : null;
        }

        if (!TryQuery(query, out var question, out var name, out var qtype, out var qclass))
        {
            return null;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(query.Span[..2]);
        if (qclass != ClassIn)
        {
            return Answer(id, question.Span, Refused, []);
        }

        // 非 .ft 的名字一律转给系统 DNS，本端只对自己负责的后缀给权威答案
        if (!name.EndsWith(PeerSocks5.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return await ForwardAsync(id, query, token);
        }

        var label = name[..^PeerSocks5.Suffix.Length];
        if (label.Length != 0 && !label.Contains('.')
            && byName.TryGetValue(label, out var address) && IPAddress.TryParse(address, out var member))
        {
            // 组网只有 IPv4：AAAA 回空答案，让应用立刻改问 A，而不是等到超时
            return Answer(id, question.Span, NoError, qtype == TypeA ? [member] : []);
        }

        return Answer(id, question.Span, NxDomain, []);
    }

    /// <summary>超长查询连问题段都不可信，只能从首部取事务编号回一个拒答。</summary>
    private static ushort QueryId(ReadOnlyMemory<byte> query)
        => query.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(query.Span[..2]) : (ushort)0;

    /// <summary>把非 .ft 的查询转给系统 DNS，答案原样带回（应用只按事务编号配对）。</summary>
    private async Task<byte[]?> ForwardAsync(ushort id, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        foreach (var server in Forwarders())
        {
            try
            {
                using var socket = new UdpClient(AddressFamily.InterNetwork);
                await socket.SendAsync(payload, server, token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ForwardTimeout);
                var reply = await socket.ReceiveAsync(timeout.Token);
                var bytes = reply.Buffer;
                // 只接受同一次查询的应答，别人塞过来的包不能转交给应用
                if (bytes.Length > 12 && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2)) == id
                    && (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2)) & 0x8000) != 0)
                {
                    return bytes;
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !token.IsCancellationRequested)
            {
                logger.LogDebug("组网 DNS 转发到 {Server} 失败：{Message}", server, ex.Message);
            }
        }

        return null;
    }

    /// <summary>系统正在用的 DNS 服务器；排除回环和本网段，免得自己转给自己。测试可以直接注入。</summary>
    private IPEndPoint[] Forwarders()
    {
        if (forwarders is { } cached)
        {
            return cached;
        }

        if (upstream is { } pinned)
        {
            forwarders = pinned;
            return pinned;
        }

        var servers = new List<IPAddress>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces().Where(card =>
                card.OperationalStatus == OperationalStatus.Up
                && card.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                try
                {
                    servers.AddRange(card.GetIPProperties().DnsAddresses);
                }
                catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException)
                {
                }
            }
        }
        else
        {
            foreach (var line in ReadResolvConf())
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && string.Equals(parts[0], "nameserver", StringComparison.OrdinalIgnoreCase)
                    && IPAddress.TryParse(parts[1], out var address))
                {
                    servers.Add(address);
                }
            }
        }

        forwarders = Filter(servers, cidr);
        return forwarders;
    }

    /// <summary>挑出能用的上游：真正可单播的 IPv4、不在组网网段里，最多三个。</summary>
    internal static IPEndPoint[] Filter(IEnumerable<IPAddress> servers, PeerNetworkCidr cidr)
        => [.. servers
            .Where(address => IsUsableServer(address) && !cidr.Contains(address))
            .Distinct()
            .Take(3)
            .Select(address => new IPEndPoint(address, DnsPort))];

    /// <summary>只认真正的 IPv4 单播服务器：0.x 未指定、127.x 回环、224 以上组播与广播都排除。</summary>
    private static bool IsUsableServer(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var first = address.GetAddressBytes()[0];
        return first is > 0 and < 127 or > 127 and < 224;
    }

    private static IEnumerable<string> ReadResolvConf()
    {
        try
        {
            return File.Exists("/etc/resolv.conf") ? File.ReadAllLines("/etc/resolv.conf") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>解析查询首部和问题段。指针压缩、多问题、非标准操作码都不接。</summary>
    private static bool TryQuery(ReadOnlyMemory<byte> wire, out ReadOnlyMemory<byte> question,
        out string name, out ushort qtype, out ushort qclass)
    {
        question = default;
        name = "";
        qtype = qclass = 0;
        var bytes = wire.Span;
        if (bytes.Length < 12)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if ((flags & 0x8000) != 0 || ((flags >> 11) & 0xF) != 0 || BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]) != 1)
        {
            return false;
        }

        var labels = new StringBuilder();
        var at = 12;
        while (at < bytes.Length)
        {
            var length = bytes[at];
            if (length == 0)
            {
                at++;
                break;
            }

            if (length >= 0xC0 || at + 1 + length > bytes.Length)
            {
                return false;
            }

            if (labels.Length != 0)
            {
                labels.Append('.');
            }

            // 标签内容原样小写化：非 ASCII 写法（punycode 或裸 UTF-8）匹配不到目录，自然走 NXDOMAIN
            for (var offset = at + 1; offset < at + 1 + length; offset++)
            {
                labels.Append(char.ToLowerInvariant((char)bytes[offset]));
            }

            at += 1 + length;
        }

        if (at + 4 > bytes.Length || labels.Length == 0 || labels.Length > 253)
        {
            return false;
        }

        qtype = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
        qclass = BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]);
        question = wire[12..(at + 4)];
        name = labels.ToString();
        return true;
    }

    /// <summary>按 DNS 报文格式拼应答：首部 + 原样回显问题 + A 记录。</summary>
    private static byte[]? Answer(ushort id, ReadOnlySpan<byte> question, byte rcode, IPAddress[] addresses)
    {
        if (question.Length > 512 || 12 + question.Length + addresses.Length * 16 > 512)
        {
            return null;
        }

        // 一条 A 记录 = 2 字节名字指针 + 2 类型 + 2 类别 + 4 TTL + 2 数据长度 + 4 地址
        var buffer = new byte[12 + question.Length + addresses.Length * 16];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(0x8000 | 0x0080 | rcode));
        // 超长查询连问题段都没法回显，问题数只能写 0
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)(question.Length == 0 ? 0 : 1));
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)addresses.Length);
        question.CopyTo(span[12..]);
        var at = 12 + question.Length;
        foreach (var address in addresses)
        {
            span[at] = 0xC0;
            span[at + 1] = 0x0C;
            BinaryPrimitives.WriteUInt16BigEndian(span[(at + 2)..], TypeA);
            BinaryPrimitives.WriteUInt16BigEndian(span[(at + 4)..], ClassIn);
            BinaryPrimitives.WriteUInt32BigEndian(span[(at + 6)..], TtlSeconds);
            BinaryPrimitives.WriteUInt16BigEndian(span[(at + 10)..], 4);
            address.GetAddressBytes().CopyTo(span[(at + 12)..]);
            at += 16;
        }

        return buffer;
    }

    /// <summary>成员编号一定认；成员名只在纯 ASCII 时认，中文名的 punycode 写法不在这里猜。</summary>
    private static Dictionary<string, string> Index(PeerNetworkRoster roster)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in roster.Peers)
        {
            if (IsAsciiLabel(entry.Id))
            {
                index[entry.Id.ToLowerInvariant()] = entry.Ip;
            }
        }

        foreach (var entry in roster.Peers)
        {
            if (IsAsciiLabel(entry.Name))
            {
                index.TryAdd(entry.Name.ToLowerInvariant(), entry.Ip);
            }
        }

        return index;
    }

    private static bool IsAsciiLabel(string value)
        => value.Length != 0 && value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    public ValueTask DisposeAsync()
    {
        closing.Cancel();
        listener?.Dispose();
        slots.Dispose();
        closing.Dispose();
        return ValueTask.CompletedTask;
    }
}
