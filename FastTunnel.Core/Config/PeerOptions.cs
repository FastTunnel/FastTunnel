using System.Globalization;
using FastTunnel.Core.P2P;

namespace FastTunnel.Core.Config;

/// <summary>私有服务、访客和 UDP 直连配置。</summary>
public sealed class PeerOptions
{
    /// <summary>尝试 UDP 直连，失败时由 WSS 中继承载连接。</summary>
    public bool EnableDirect { get; set; } = true;

    /// <summary>以 Information 级别记录每条访客连接使用的直连或中继路径。</summary>
    public bool LogConnectionPath { get; set; }

    /// <summary>允许 UDP 直连。关闭时仍可尝试 TCP/TLS 直连。</summary>
    public bool EnableUdpDirect { get; set; } = true;

    /// <summary>可配置多个 STUN URI，例如 stun:stun.example.com:3478。</summary>
    public string[] StunServers { get; set; } = [];

    /// <summary>直连使用的固定 UDP 端口，0 为自动分配。</summary>
    public int DirectUdpPort { get; set; }

    /// <summary>可选的 TCP/TLS 直连监听端口，0 表示只主动连接对端。</summary>
    public int DirectTcpPort { get; set; }

    /// <summary>手动映射的 TCP 公网地址和端口。支持 IPv4 或带方括号的 IPv6。</summary>
    public string? DirectTcpEndpoint { get; set; }

    /// <summary>允许通过 PCP、NAT-PMP 或 UPnP IGD 为 P2P UDP 端口申请临时网关映射。</summary>
    public bool AutoMapPort { get; set; }

    /// <summary>与主服务器并行预连的额外 WSS 协调节点。各节点需配置相同的客户端主体和 Token。</summary>
    public SuiDaoServer[] AdditionalRelays { get; set; } = [];

    /// <summary>发布回环 TCP 服务的简写。键为服务名，值为「端口@访客ID」，多个 ID 用逗号分隔；
    /// 端口位也支持「22,3389」「8000-8100」「*」，此时由访客指定实际端口。</summary>
    public Dictionary<string, string> Share { get; set; } = new();

    /// <summary>访问私有服务的简写。键为「提供端ID/服务名」，值为本机回环监听端口；
    /// 提供端放行端口范围时，键写成「提供端ID/服务名/远端端口」。</summary>
    public Dictionary<string, string> Connect { get; set; } = new();

    /// <summary>本客户端发布的服务。</summary>
    public PeerServiceOptions[] Services { get; set; } = [];

    /// <summary>本客户端访问的服务。</summary>
    public PeerVisitorOptions[] Visitors { get; set; } = [];

    /// <summary>访客本机的 SOCKS5 出口端口，0 表示不启用。启用后目标写成「服务名.提供端ID.ft」，
    /// 端口由应用自己决定，无需为每个远端端口在本机开监听。</summary>
    public int Socks5 { get; set; }

    /// <summary>SOCKS5 出口可用的「提供端ID/服务名」列表；留空表示复用 Visitors/Connect 里的全部通道。</summary>
    public string[] Socks5Via { get; set; } = [];

    /// <summary>启用 L3 组网：向服务端领取虚拟 IP，创建虚拟网卡，把网段内的流量直接走 P2P。
    /// 默认关闭——只用 Web / TCP 穿透时无需虚拟网卡，也不用以管理员运行。
    /// 开启需要服务端同时配置 <c>FastTunnel:P2PNetwork:Enabled=true</c>，且设备是通过入网码入网的。</summary>
    public bool EnableL3 { get; set; }

    /// <summary>虚拟网卡名称；留空时 Linux 用 <c>ft0</c>、Windows 用 <c>FastTunnel</c>。
    /// 只允许字母、数字和 <c>- _ .</c>，最长 15 字符。</summary>
    public string? L3Interface { get; set; }

    /// <summary>虚拟网卡 MTU，0 表示使用 1400（为直连封装留出余量）；超出 576-1500 的配置会被收敛到边界。</summary>
    public int L3Mtu { get; set; }

    /// <summary>接管 <c>.ft</c> 域名的解析：占住本端虚拟地址的 53 端口，查 <c>成员ID.ft</c> 直接得到
    /// 它的虚拟地址，其余域名转给系统原本在用的 DNS。关闭后组网照样能通，只是要自己写 hosts。</summary>
    public bool L3Dns { get; set; } = true;

    internal bool HasEndpoints => Services.Length != 0 || Visitors.Length != 0 || Share.Count != 0
        || Connect.Count != 0 || Socks5 != 0 || EnableL3;

    internal PeerServiceOptions[] GetServices() => Services.Concat(Share.Select(entry =>
    {
        var parts = entry.Value?.Split('@', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } || parts[1].Length == 0)
            throw ShareError(entry.Key);

        var service = new PeerServiceOptions
        {
            Name = entry.Key,
            AllowVisitors = parts[1].Split(',', StringSplitOptions.TrimEntries),
        };

        // 单个端口沿用固定转发；写成列表或区间时端口由访客在开流时指定
        if (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            if (port is < 1 or > 65535)
                throw ShareError(entry.Key);
            service.LocalPort = port;
        }
        else if (PeerPortPolicy.Parse(parts[0]) != null)
        {
            service.Ports = parts[0];
        }
        else
        {
            throw ShareError(entry.Key);
        }

        return service;
    })).ToArray();

    private static InvalidOperationException ShareError(string name) => new(
        $"P2P Share 的服务「{name}」应填写「端口@访客ID」，例如 22@laptop、3389,8000-8100@laptop、*@laptop");

    internal PeerVisitorOptions[] GetVisitors() => Visitors.Concat(Connect.Select(entry =>
    {
        var parts = entry.Key.Split('/');
        if (parts.Length is not (2 or 3) || parts.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("P2P Connect 的目标应填写「提供端ID/服务名」，例如 home/ssh；要指定远端端口再写 /端口，例如 home/dev/3389");

        if (!int.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new InvalidOperationException($"P2P Connect 的目标「{entry.Key}」应填写本机监听端口，例如 2222");

        var remote = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out remote)
                || remote is < 1 or > 65535)
            {
                throw new InvalidOperationException($"P2P Connect 的目标「{entry.Key}」远端端口无效，例如 home/dev/3389");
            }
        }

        return new PeerVisitorOptions { Provider = parts[0], Service = parts[1], ListenPort = port, RemotePort = remote };
    })).ToArray();
}

/// <summary>将提供端本地 TCP 服务发布给授权主体。</summary>
public sealed class PeerServiceOptions
{
    /// <summary>当前凭据主体下唯一的服务名。</summary>
    public string Name { get; set; } = "";

    /// <summary>提供端连接的本地地址。</summary>
    public string LocalHost { get; set; } = "127.0.0.1";

    /// <summary>提供端连接的本地端口。</summary>
    public int LocalPort { get; set; }

    /// <summary>允许访客自行指定的端口写法，例如 <c>"22,3389"</c>、<c>"8000-8100"</c>、<c>"*"</c>。
    /// 配置后 <see cref="LocalPort"/> 忽略，实际端口来自访客；始终只连 <see cref="LocalHost"/>。</summary>
    public string? Ports { get; set; }

    /// <summary>可访问该服务的服务端 Clients Id。空列表禁止所有访问。</summary>
    public string[] AllowVisitors { get; set; } = [];
}

/// <summary>在访客本机建立私有服务的 TCP 入口。</summary>
public sealed class PeerVisitorOptions
{
    /// <summary>提供端在服务端配置中的 Clients Id。</summary>
    public string Provider { get; set; } = "";

    /// <summary>提供端发布的服务名。</summary>
    public string Service { get; set; } = "";

    /// <summary>访客本机的回环监听地址。</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>访客本机的监听端口。</summary>
    public int ListenPort { get; set; }

    /// <summary>要访问的提供端端口；0 表示使用提供端为该服务配置的固定端口。
    /// 仅当提供端用 <c>Ports</c> 放行了端口范围时生效，且必须落在该范围内。</summary>
    public int RemotePort { get; set; }
}
