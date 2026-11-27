using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

/// <summary>
/// L3 组网泵。本机虚拟网卡上发往网段的 IP 包按目的地址投给对应成员，成员送来的包再交回网卡；
/// TCP / UDP 状态仍由两端内核负责，这里只搬运完整的 IPv4 包。
/// 通道没就绪时直接丢包，让内核重传，避免为不在线的设备积压数据。
/// </summary>
internal sealed class PeerNetwork(PeerNetworkRoster roster, ITunDevice device, ILogger logger)
{
    /// <summary>IPv4 包的理论上限；虚拟网卡一次最多读这么多。</summary>
    private const int MaxRead = 65535;

    private readonly ConcurrentDictionary<string, Channel> channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly PeerNetworkCidr cidr = PeerNetworkCidr.Parse(roster.Cidr)!;
    private readonly IPAddress? self = ParseOwn(roster);

    internal string SelfId => roster.Self;

    /// <summary>本端在网段里的地址，由服务端组网目录发放。</summary>
    internal string VirtualIp { get; } = FindOwn(roster);

    internal string DeviceName => device.Name;

    /// <summary>该成员是否已有可用通道。上层据此决定是补连还是复用。</summary>
    internal bool HasChannel(string peerId) => channels.ContainsKey(peerId);

    /// <summary>网段里除自己之外的成员编号，上层按它为每个人维护一条通道。</summary>
    internal string[] Peers => [.. roster.Peers
        .Select(x => x.Id)
        .Where(id => !string.Equals(id, roster.Self, StringComparison.OrdinalIgnoreCase))];

    /// <summary>为某个成员开 MUX 流时要写的目标：把本端地址写在 Host 里，对端据此认领通道。</summary>
    internal SwapRequest NewTarget() => new()
    {
        Id = PeerProtocol.PrivateId,
        Host = PeerProtocol.IpService + "/" + VirtualIp,
        Port = 1,
    };

    /// <summary>
    /// 校验对端 SYN 里声明的地址并换算成成员编号。地址必须出现在组网目录里，
    /// 否则任何成员都能谎报身份，把流量塞进别人的地址。
    /// </summary>
    internal bool TryAdmit(SwapRequest target, out string peerId)
    {
        peerId = "";
        var prefix = PeerProtocol.IpService + "/";
        if (!target.Host.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var announced = target.Host[prefix.Length..];
        if (!IPAddress.TryParse(announced, out var address) || !cidr.Contains(address)
            || address.Equals(self))
        {
            return false;
        }

        var entry = Array.Find(roster.Peers, peer => string.Equals(peer.Ip, announced, StringComparison.OrdinalIgnoreCase));
        return entry != null && !string.Equals(peerId = entry.Id, roster.Self, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>网卡 → 成员通道。运行到设备关闭或 token 取消。</summary>
    internal async Task RunAsync(CancellationToken token)
    {
        var buffer = GC.AllocateArray<byte>(MaxRead, pinned: true);
        while (true)
        {
            var length = await device.ReadAsync(buffer, token);
            if (length <= 0)
            {
                return;
            }

            // 对端与内核送来的包都按不可信输入处理：解析失败、越界、发给自己的都不转发
            if (!PeerIpPacket.TryParse(buffer.AsMemory(0, length), out var packet)
                || packet.TotalLength > UdpDatagramFramer.MaxDatagramSize
                || packet.Destination.Equals(self) || !cidr.Contains(packet.Destination))
            {
                continue;
            }

            var peerId = PeerIdOf(packet.Destination);
            if (peerId.Length == 0)
            {
                continue;
            }

            if (!channels.TryGetValue(peerId, out var channel))
            {
                if (warned.TryAdd(peerId, 0))
                {
                    logger.LogInformation("P2P 组网到 {Peer} 的通道未就绪，先丢弃发往 {Host} 的包（内核会自动重传）",
                        peerId, packet.Destination);
                }

                continue;
            }

            warned.TryRemove(peerId, out _);
            try
            {
                // 整包发送，但只搬运首部声明的长度：以太网填充的尾部字节属于链路层
                await channel.Stream.WriteDatagramAsync(buffer.AsMemory(0, packet.TotalLength), token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException && !token.IsCancellationRequested)
            {
                Close(peerId, channel, ex);
            }
        }
    }

    /// <summary>承载一个成员的双向通道：对端送来的包写回网卡，本端发出的包由 <see cref="RunAsync"/> 投递。</summary>
    internal async Task ServeAsync(string peerId, MuxStream stream, CancellationToken token)
    {
        var ip = IpOf(peerId);
        if (ip.Length == 0)
        {
            logger.LogWarning("组网目录里没有成员 {Peer}，拒绝建立通道", peerId);
            await stream.DisposeAsync();
            return;
        }

        var channel = new Channel(ip, stream);
        if (!channels.TryAdd(peerId, channel))
        {
            // 两端同时主动连接时会出现第二条通道，保留先到的一条，避免同一对成员互相抢路由
            logger.LogDebug("P2P 组网成员 {Peer} 已有通道，丢弃重复连接", peerId);
            await stream.DisposeAsync();
            return;
        }

        logger.LogInformation("P2P 组网通道已就绪：{Peer}（{Ip}）经 {Device}", peerId, ip, device.Name);
        Exception? failure = null;
        try
        {
            await ReceiveAsync(peerId, channel, token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException
            && !token.IsCancellationRequested)
        {
            failure = ex;
        }
        finally
        {
            Close(peerId, channel, failure);
            await stream.DisposeAsync();
        }
    }

    private async Task ReceiveAsync(string peerId, Channel channel, CancellationToken token)
    {
        var stream = channel.Stream;
        var scratch = ArrayPool<byte>.Shared.Rent(UdpDatagramFramer.MaxDatagramSize);
        try
        {
            while (true)
            {
                var result = await stream.Input.ReadAsync(token);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                try
                {
                    while (UdpDatagramFramer.TryRead(buffer, scratch, out var packet, out var total))
                    {
                        // 对端可以声明任何地址，但包里的源地址必须是它自己，否则等于让它冒充别的成员
                        if (BelongsTo(channel.Ip, packet))
                        {
                            await device.WriteAsync(packet, token);
                        }

                        buffer = buffer.Slice(total);
                        consumed = buffer.Start;
                    }
                }
                finally
                {
                    stream.Input.AdvanceTo(consumed, buffer.End);
                }

                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            stream.Input.Complete();
        }
    }

    /// <summary>只放行本通道对应成员发出、且确实属于本网段或发给自己的包。</summary>
    private bool BelongsTo(string peerIp, ReadOnlyMemory<byte> packet)
        => PeerIpPacket.TryParse(packet, out var parsed)
            && string.Equals(parsed.Source.ToString(), peerIp, StringComparison.OrdinalIgnoreCase)
            && (parsed.Destination.Equals(self) || cidr.Contains(parsed.Destination));

    private void Close(string peerId, Channel channel, Exception? error)
    {
        if (!channels.TryRemove(peerId, out var current) || !ReferenceEquals(current, channel))
        {
            return;
        }

        warned.TryRemove(peerId, out _);
        logger.LogInformation(error is null
            ? "P2P 组网通道 {Peer} 已关闭"
            : "P2P 组网通道 {Peer} 已关闭：{Message}", peerId, error.Message);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in channels.Values)
        {
            await channel.Stream.DisposeAsync();
        }

        channels.Clear();
        await device.DisposeAsync();
    }

    private string PeerIdOf(IPAddress address) => Array
        .Find(roster.Peers, peer => string.Equals(peer.Ip, address.ToString(), StringComparison.OrdinalIgnoreCase))?.Id ?? "";

    private string IpOf(string peerId) => Array
        .Find(roster.Peers, peer => string.Equals(peer.Id, peerId, StringComparison.OrdinalIgnoreCase))?.Ip ?? "";

    /// <summary>目录里必须有自己，否则虚拟网卡无从配置。</summary>
    private static string FindOwn(PeerNetworkRoster roster) => IpOf(roster, roster.Self);

    private static IPAddress? ParseOwn(PeerNetworkRoster roster)
        => IPAddress.TryParse(IpOf(roster, roster.Self), out var address) ? address : null;

    private static string IpOf(PeerNetworkRoster roster, string peerId) => Array
        .Find(roster.Peers, peer => string.Equals(peer.Id, peerId, StringComparison.OrdinalIgnoreCase))?.Ip ?? "";

    private sealed class Channel(string ip, MuxStream stream)
    {
        /// <summary>该成员在组网目录里的地址，用于校验对端送来的包的源地址。</summary>
        internal string Ip { get; } = ip;

        internal MuxStream Stream { get; } = stream;
    }
}
