namespace FastTunnel.Core.P2P;

/// <summary>
/// L3 组网目录：客户端配置虚拟网卡需要的网段、自己的地址和成员表。
/// 走 <see cref="PeerProtocol.NetworkPath"/>，与 P2P 信令同一个端口、同一张证书、同一种 Token 校验。
/// </summary>
public sealed class PeerNetworkRoster
{
    /// <summary>虚拟网段（已规范化的 CIDR 写法）。</summary>
    public string Cidr { get; set; } = "";

    /// <summary>请求方自己的主体编号。</summary>
    public string Self { get; set; } = "";

    /// <summary>网段内所有在册且已启用设备的成员表。</summary>
    public PeerNetworkEntry[] Peers { get; set; } = [];
}

/// <summary>组网目录里的一个成员。</summary>
public sealed class PeerNetworkEntry
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Ip { get; set; } = "";
}
