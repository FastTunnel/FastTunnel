using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 访客本机的 SOCKS5 出口：应用把代理指到 <c>127.0.0.1:Socks5</c>，目标写成
/// <c>服务名.提供端ID.ft</c>（例：<c>dev.home.ft</c>）就能按应用给出的端口连到提供端。
/// 端口由应用原样带过来，落在提供端放行的范围内即可，不必再为本机逐条开监听端口。
/// 只支持 CONNECT、无鉴权，且只监听回环地址。
/// </summary>
internal static class PeerSocks5
{
    internal const string Suffix = ".ft";
    internal const int MaxExits = 16;
    private const int MaxDomainLength = 253;
    private const byte Version = 0x05;
    private const byte CommandConnect = 0x01;
    private const byte TypeIPv4 = 0x01;
    private const byte TypeDomain = 0x03;
    private const byte TypeIPv6 = 0x04;
    private const byte FailureGeneral = 0x01;
    private const byte FailureNotAllowed = 0x02;
    private const byte FailureHostUnreachable = 0x04;
    private const byte FailureRefused = 0x05;
    private const byte FailureCommand = 0x07;
    private const byte FailureAddressType = 0x08;

    /// <summary>一个可选出口，对应一条已配置的 P2P 访客通道。</summary>
    internal readonly record struct Exit(string Provider, string Service);

    /// <summary>SOCKS5 的可用出口：显式配了 Socks5Via 就用它，否则取已配置的 Visitors/Connect 通道。</summary>
    internal static Exit[] ResolveExits(PeerOptions peer, PeerVisitorOptions[] visitors)
        => ParseExits(peer.Socks5Via.Length != 0
            ? peer.Socks5Via
            : visitors.Select(visitor => visitor.Provider + "/" + visitor.Service).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>解析 Via 里的「提供端ID/服务名」；写法不合法、重复或数量超限时抛异常。</summary>
    internal static Exit[] ParseExits(IEnumerable<string> via)
    {
        var exits = new List<Exit>();
        foreach (var entry in via)
        {
            var parts = entry?.Split('/', StringSplitOptions.TrimEntries);
            if (parts is not { Length: 2 } || !PeerProtocol.IsName(parts[0]) || !PeerProtocol.IsName(parts[1]))
                throw new InvalidOperationException($"P2P Socks5 Via 应填写「提供端ID/服务名」，例如 home/dev，当前是「{entry}」");

            var exit = new Exit(parts[0], parts[1]);
            if (exits.Contains(exit))
                throw new InvalidOperationException($"P2P Socks5 Via 重复填写了「{entry}」");
            exits.Add(exit);
        }

        if (exits.Count is 0 or > MaxExits)
            throw new InvalidOperationException($"P2P Socks5 需要 1–{MaxExits} 个 Via 出口");

        return exits.ToArray();
    }

    /// <summary>
    /// 主机名 → 出口。<c>服务名.提供端ID.ft</c> 精确匹配，只写 <c>提供端ID.ft</c> 时要求该提供端只有一个出口；
    /// 其他名字（IP、真实域名）只在总共只有一个出口时放行。匹配不上就拒绝，不让凭据变成试探内网的入口。
    /// </summary>
    internal static bool TryResolve(string host, Exit[] exits, out int index)
    {
        index = -1;
        if (!host.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            if (exits.Length != 1)
                return false;
            index = 0;
            return true;
        }

        var labels = host[..^Suffix.Length];
        if (labels.Length == 0)
            return false;

        var dot = labels.LastIndexOf('.');
        var provider = dot < 0 ? labels : labels[(dot + 1)..];
        var service = dot < 0 ? "" : labels[..dot];
        var candidates = new List<int>();
        for (var i = 0; i < exits.Length; i++)
            if (exits[i].Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                candidates.Add(i);

        if (service.Length == 0)
        {
            if (candidates.Count != 1)
                return false;
            index = candidates[0];
            return true;
        }

        foreach (var i in candidates)
        {
            if (exits[i].Service.Equals(service, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 读完 SOCKS5 握手并解析出目标主机与端口。逐字节精确收取，绝不越界读到业务数据，
    /// 返回后调用方可以直接把同一个 Socket 交给数据面桥接。版本/命令不合法时抛
    /// <see cref="IOException"/>，并已按协议写好拒绝报文。
    /// </summary>
    internal static async Task<(string Host, int Port)> ReadRequestAsync(Socket network, CancellationToken token)
    {
        var header = await ReadAsync(network, 2, token);
        if (header[0] != Version)
            throw new IOException("SOCKS5 版本不匹配");

        var methods = await ReadAsync(network, header[1], token);
        if (!methods.Contains((byte)0x00))
        {
            await SendAsync(network, new byte[] { Version, 0xFF }, token);
            throw new IOException("SOCKS5 出口只支持无鉴权");
        }
        await SendAsync(network, new byte[] { Version, 0x00 }, token);

        var request = await ReadAsync(network, 4, token);
        if (request[0] != Version)
            throw new IOException("SOCKS5 请求版本不匹配");
        if (request[1] != CommandConnect)
        {
            await WriteFailureAsync(network, FailureCommand, token);
            throw new IOException("SOCKS5 出口只支持 CONNECT");
        }

        string host;
        switch (request[3])
        {
            case TypeIPv4:
                host = new IPAddress(await ReadAsync(network, 4, token)).ToString();
                break;
            case TypeIPv6:
                host = new IPAddress(await ReadAsync(network, 16, token)).ToString();
                break;
            case TypeDomain:
                {
                    var length = (await ReadAsync(network, 1, token))[0];
                    if (length is 0 or > MaxDomainLength)
                    {
                        await WriteFailureAsync(network, FailureAddressType, token);
                        throw new IOException("SOCKS5 域名长度无效");
                    }

                    host = Encoding.ASCII.GetString(await ReadAsync(network, length, token));
                    break;
                }
            default:
                await WriteFailureAsync(network, FailureAddressType, token);
                throw new IOException("SOCKS5 只支持 IPv4 / IPv6 / 域名目标");
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(network, 2, token));
        return (host, port);
    }

    /// <summary>隧道已建立，回成功报文（绑定地址与端口按惯例填 0）。</summary>
    internal static Task WriteSuccessAsync(Socket network, CancellationToken token)
        => SendAsync(network, new byte[] { Version, 0x00, 0x00, TypeIPv4, 0, 0, 0, 0, 0, 0 }, token);

    internal static Task WriteNotAllowedAsync(Socket network, CancellationToken token)
        => WriteFailureAsync(network, FailureNotAllowed, token);

    internal static Task WriteHostUnreachableAsync(Socket network, CancellationToken token)
        => WriteFailureAsync(network, FailureHostUnreachable, token);

    internal static Task WriteRefusedAsync(Socket network, CancellationToken token)
        => WriteFailureAsync(network, FailureRefused, token);

    internal static Task WriteGeneralFailureAsync(Socket network, CancellationToken token)
        => WriteFailureAsync(network, FailureGeneral, token);

    private static async Task SendAsync(Socket network, byte[] reply, CancellationToken token)
        => await network.SendAsync(reply, SocketFlags.None, token);

    private static Task WriteFailureAsync(Socket network, byte failure, CancellationToken token)
        => SendAsync(network, new byte[] { Version, failure, 0x00, TypeIPv4, 0, 0, 0, 0, 0, 0 }, token);

    private static async Task<byte[]> ReadAsync(Socket network, int length, CancellationToken token)
    {
        if (length == 0)
            throw new IOException("SOCKS5 握手不完整");

        var buffer = new byte[length];
        var written = 0;
        while (written < length)
        {
            var read = await network.ReceiveAsync(buffer.AsMemory(written), token);
            if (read == 0)
                throw new IOException("SOCKS5 连接被中断");
            written += read;
        }

        return buffer;
    }
}
