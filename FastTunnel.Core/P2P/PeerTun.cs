using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 按平台创建虚拟网卡：Linux 直接 ioctl <c>/dev/net/tun</c>，Windows 走 wintun.dll。
/// 其余平台、权限不足或缺少驱动时返回 null，调用方只会少掉 L3 功能，其余 P2P 能力不受影响。
/// </summary>
internal static class PeerTun
{
    /// <summary>接口名只允许这些字符：它会被拼进系统命令的参数里。</summary>
    private static readonly char[] AllowedNameChars = ['-', '_', '.'];

    /// <summary>默认的链路 MTU：留出隧道封装的余量，避免两端内核再分片。</summary>
    internal const int DefaultMtu = 1400;

    internal const string LinuxDefaultName = "ft0";
    internal const string WindowsDefaultName = "FastTunnel";

    internal static ITunDevice? Open(PeerNetworkRoster roster, PeerOptions options, ILogger logger)
    {
        var cidr = PeerNetworkCidr.Parse(roster.Cidr);
        var own = Array.Find(roster.Peers, peer => string.Equals(peer.Id, roster.Self, StringComparison.OrdinalIgnoreCase));
        if (cidr == null || own is null || !cidr.TryAddress(own.Ip, out _))
        {
            logger.LogWarning("组网目录缺少本端虚拟地址，L3 组网未启用");
            return null;
        }

        var requested = string.IsNullOrWhiteSpace(options.L3Interface)
            ? (OperatingSystem.IsWindows() ? WindowsDefaultName : LinuxDefaultName)
            : options.L3Interface.Trim();
        if (!IsInterfaceName(requested))
        {
            logger.LogWarning("FastTunnel:P2P:L3Interface「{Name}」非法：只允许 1–15 位字母、数字、-_.", requested);
            return null;
        }

        var setup = new TunSetup(requested, own.Ip, cidr.NetMask().ToString(), cidr.PrefixLength,
            options.L3Mtu is > 0 ? Math.Clamp(options.L3Mtu, 576, 1500) : DefaultMtu);
        if (OperatingSystem.IsLinux())
        {
            return LinuxTun.TryCreate(setup, logger);
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsTun.TryCreate(setup, logger);
        }

        logger.LogWarning("当前系统还没有虚拟网卡实现，L3 组网未启用；任意端口访问请改用 P2P:Socks5 出口");
        return null;
    }

    private static bool IsInterfaceName(string name)
        => name is { Length: > 0 and <= 15 }
            && name.All(c => char.IsAsciiLetterOrDigit(c) || AllowedNameChars.Contains(c));
}
