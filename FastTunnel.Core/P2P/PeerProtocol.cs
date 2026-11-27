using System.Net.WebSockets;
using System.Text;

namespace FastTunnel.Core.P2P;

internal static class PeerProtocol
{
    internal const string Path = "/p2p/v1";

    /// <summary>L3 组网目录：GET，用与 P2P 信令相同的 Token 请求头鉴权。</summary>
    internal const string NetworkPath = "/p2p/v1/network";

    /// <summary>承载虚拟网卡流量的保留服务名，成员之间不用为它写配置。</summary>
    internal const string IpService = "ip";

    /// <summary>私有通道上 MUX 流的保留标识，访客与提供端都靠它区分普通转发与 P2P。
    /// </summary>
    internal const string PrivateId = "p2p";

    internal const string Service = "FT_P2P_SERVICE";
    internal const string Provider = "FT_P2P_PROVIDER";
    internal const string Visitors = "FT_P2P_VISITORS";
    internal const string Direct = "FT_P2P_DIRECT";
    internal const string DirectVersion = "4";
    internal const int ChunkSize = 16 * 1024;
    internal const int MaxSignalSize = 48 * 1024;

    internal static bool IsName(string? value)
        => value is { Length: > 0 and <= 64 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static bool IsPort(int port) => port is > 0 and <= 65535;

    internal static async Task SendTextAsync(WebSocket socket, string value, CancellationToken token)
        => await socket.SendAsync(Encoding.UTF8.GetBytes(value).AsMemory(), WebSocketMessageType.Text, true, token);

    internal static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (frame.MessageType == WebSocketMessageType.Close)
                return null;

            if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > MaxSignalSize)
                throw new IOException("无效的 P2P 信令消息");

            message.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
}
