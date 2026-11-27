using System.Globalization;
using System.Linq;
using FastTunnel.Core.Config;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 提供端放行给访客的端口范围。不配置 <c>Ports</c> 时只提供 <c>LocalPort</c> 这一个端口；
/// 配置后访客在开流时自带目标端口，超出范围一律拒绝——否则一条凭据就成了
/// 对提供端整机的端口扫描器。
/// </summary>
internal sealed class PeerPortPolicy
{
    private const int MaxEntries = 16;

    private readonly (int Start, int End)[] ranges;

    private PeerPortPolicy((int Start, int End)[] ranges) => this.ranges = ranges;

    /// <summary>写法只落到一个端口时可以忽略访客请求的端口，按配置连。</summary>
    internal int? FixedPort => ranges is [{ Start: var start, End: var end }] && start == end ? start : null;

    /// <summary>解析 <c>"22"</c>、<c>"22,3389"</c>、<c>"8000-8100"</c>、<c>"*"</c>；不合法返回 null。</summary>
    internal static PeerPortPolicy? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > MaxEntries)
            return null;
        if (parts.Length == 1 && parts[0] == "*")
            return new([(1, 65535)]);

        var list = new (int Start, int End)[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var dash = parts[i].IndexOf('-');
            int start;
            int end;
            if (dash < 0)
            {
                if (!TryPort(parts[i], out start))
                    return null;
                end = start;
            }
            else if (!TryPort(parts[i][..dash], out start) || !TryPort(parts[i][(dash + 1)..], out end))
            {
                return null;
            }

            if (start > end)
                return null;
            list[i] = (start, end);
        }

        return new(list);
    }

    internal bool Allows(int port) => ranges.Any(x => port >= x.Start && port <= x.End);

    /// <summary>未写 Ports 的服务退化成只提供 LocalPort 一个端口，访客请求的端口一律忽略。</summary>
    internal static PeerPortPolicy For(PeerServiceOptions service)
        => Parse(service.Ports) ?? new PeerPortPolicy([(service.LocalPort, service.LocalPort)]);

    private static bool TryPort(string value, out int port)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
