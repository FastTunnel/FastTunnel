using System.Globalization;

namespace FastTunnel.Core.Config;

/// <summary>私有服务、访客和 UDP 直连配置。</summary>
public sealed class PeerOptions
{
    /// <summary>尝试 UDP 直连，失败时由 WSS 中继承载连接。</summary>
    public bool EnableDirect { get; set; } = true;

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

    /// <summary>发布回环 TCP 服务的简写。键为服务名，值为「端口@访客ID」，多个 ID 用逗号分隔。</summary>
    public Dictionary<string, string> Share { get; set; } = new();

    /// <summary>访问私有服务的简写。键为「提供端ID/服务名」，值为本机回环监听端口。</summary>
    public Dictionary<string, string> Connect { get; set; } = new();

    /// <summary>本客户端发布的服务。</summary>
    public PeerServiceOptions[] Services { get; set; } = [];

    /// <summary>本客户端访问的服务。</summary>
    public PeerVisitorOptions[] Visitors { get; set; } = [];

    internal bool HasEndpoints => Services.Length != 0 || Visitors.Length != 0 || Share.Count != 0 || Connect.Count != 0;

    internal PeerServiceOptions[] GetServices() => Services.Concat(Share.Select(entry =>
    {
        var parts = entry.Value?.Split('@', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } || parts[1].Length == 0
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new InvalidOperationException($"P2P Share 的服务「{entry.Key}」应填写「端口@访客ID」，例如 22@laptop");

        return new PeerServiceOptions
        {
            Name = entry.Key,
            LocalPort = port,
            AllowVisitors = parts[1].Split(',', StringSplitOptions.TrimEntries),
        };
    })).ToArray();

    internal PeerVisitorOptions[] GetVisitors() => Visitors.Concat(Connect.Select(entry =>
    {
        var parts = entry.Key.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("P2P Connect 的目标应填写「提供端ID/服务名」，例如 home/ssh");

        if (!int.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new InvalidOperationException($"P2P Connect 的目标「{entry.Key}」应填写本机监听端口，例如 2222");

        return new PeerVisitorOptions { Provider = parts[0], Service = parts[1], ListenPort = port };
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
}
