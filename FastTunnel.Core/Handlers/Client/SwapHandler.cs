// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Sockets;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Client;

public class SwapHandler : IClientHandler
{
    private const int MaxDatagramSize = 65507;
    private static readonly TimeSpan UdpIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<SwapHandler> _logger;

    public SwapHandler(ILogger<SwapHandler> logger)
    {
        _logger = logger;
    }

    public int SwapCount = 0;

    public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken)
    {
        string requestId = null;
        try
        {
            Interlocked.Increment(ref SwapCount);

            // 新版 JSON 与旧版竖线文本统一在这里解析，服务端降级下发时同样可用。
            var request = ControlProtocol.ParseSwapRequest(msg);
            requestId = request.Id;

            _logger.LogDebug($"========Swap Start:{requestId} {DescribeProto(request)}==========");

            using var serverStream = await createRemote(requestId, cleint, cancellationToken);
            await BridgeAsync(request, serverStream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Swap error {requestId}");
        }
        finally
        {
            Interlocked.Decrement(ref SwapCount);
            _logger.LogDebug($"========Swap End:{requestId} {SwapCount}==========");
        }
    }

    /// <summary>
    /// 把一条已经就绪的隧道流与内网目标对接。
    /// 旧路径的隧道流来自 PROXY 回连，多路复用的隧道流直接是 <see cref="Mux.MuxStream"/>，
    /// 两者都实现 <see cref="Stream"/>，因此桥接逻辑完全共用。
    /// </summary>
    public async Task BridgeAsync(SwapRequest request, Stream serverStream, CancellationToken cancellationToken)
    {
        if (string.Equals(request.Proto, "udp", StringComparison.OrdinalIgnoreCase))
        {
            await BridgeUdpAsync(request.Host, request.Port, serverStream, cancellationToken);
            return;
        }

        using var localStream = await createLocal(request.Id, request.Host, request.Port, cancellationToken);

        // 使用一个与双向拷贝联动的 CTS，任一方向结束后立即取消另一方向，避免半关闭后空转。
        using var swapCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var taskX = serverStream.CopyToAsync(localStream, swapCts.Token);
        var taskY = localStream.CopyToAsync(serverStream, swapCts.Token);

        try
        {
            await Task.WhenAny(taskX, taskY);
        }
        finally
        {
            swapCts.Cancel();
        }
    }

    private static string DescribeProto(SwapRequest request)
        => string.Equals(request.Proto, "udp", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";

    /// <summary>
    /// UDP 桥接：服务端的 swap 流 ↔ 内网 UDP 端点
    /// 数据帧格式：[2 字节大端长度][数据]
    /// </summary>
    private async Task BridgeUdpAsync(string host, int port, Stream serverStream, CancellationToken cancellationToken)
    {
        // 目标可能是域名、IPv4 或 IPv6 字面量，统一走双栈解析
        var addresses = await NetUtil.ResolveAsync(host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new Exception($"无法解析 {host}");
        }

        var ip = NetUtil.OrderByPreference(addresses).First();
        var localTarget = new IPEndPoint(ip, port);

        // UDP socket 的 family 必须与目标一致，否则发不出去（v6 目标不能用 v4 socket）
        var bindAddress = ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        using var udp = new UdpClient(new IPEndPoint(bindAddress, 0));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastActive = DateTime.UtcNow;

        async Task TunnelToLocalAsync()
        {
            var header = new byte[2];
            var buffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(serverStream, header, 0, 2, cts.Token))
                        break;

                    int len = BinaryPrimitives.ReadUInt16BigEndian(header);
                    if (len > MaxDatagramSize) break; // 协议异常，避免越界
                    if (len > 0 && !await ReadExactAsync(serverStream, buffer, 0, len, cts.Token))
                        break;

                    lastActive = DateTime.UtcNow;
                    try
                    {
                        await udp.SendAsync(buffer.AsMemory(0, len), localTarget, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[UdpSwap]send to local error");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                cts.Cancel();
            }
        }

        async Task LocalToTunnelAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await udp.ReceiveAsync(cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    var data = result.Buffer;
                    if (data.Length > MaxDatagramSize) continue;

                    // 拼接 [长度 + 负载] 到一个池化缓冲区，只调用一次 WriteAsync，减少 await 次数与头部阻塞。
                    int frameLen = 2 + data.Length;
                    var frame = ArrayPool<byte>.Shared.Rent(frameLen);
                    try
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)data.Length);
                        if (data.Length > 0)
                        {
                            Buffer.BlockCopy(data, 0, frame, 2, data.Length);
                        }

                        lastActive = DateTime.UtcNow;
                        await serverStream.WriteAsync(frame.AsMemory(0, frameLen), cts.Token);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame);
                    }
                }
            }
            finally
            {
                cts.Cancel();
            }
        }

        async Task IdleMonitorAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
                    if (DateTime.UtcNow - lastActive > UdpIdleTimeout)
                    {
                        cts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        await Task.WhenAny(TunnelToLocalAsync(), LocalToTunnelAsync(), IdleMonitorAsync());
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer, offset + read, count - read, token);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private async Task<Stream> createLocal(string requestId, string host, int port, CancellationToken cancellationToken)
    {
        var socket = await DnsSocketFactory.ConnectAsync(host, port, cancellationToken);
        return new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };
    }

    private async Task<Stream> createRemote(string requestId, FastTunnelClient cleint, CancellationToken cancellationToken)
    {
        var socket = await DnsSocketFactory.ConnectAsync(
            cleint.Server.ServerAddr, cleint.Server.ServerPort, cancellationToken);
        Stream serverStream = new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };

        // 数据通道与服务端用同一个端口，服务端按首字节识别 TLS，因此这里透明加解密即可。
        if (cleint.Server.UseTls)
        {
            serverStream = await TunnelTlsClient.AuthenticateAsync(
                serverStream, cleint.Server, _logger, cancellationToken);
        }

        // IPv6 服务端要写成 [::1]:1270，裸拼会得到非法的 Host 头
        var host = NetUtil.FormatHost(cleint.Server.ServerAddr);
        var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {host}:{cleint.Server.ServerPort}\r\n\r\n";

        var requestMsg = Encoding.UTF8.GetBytes(reverse);
        await serverStream.WriteAsync(requestMsg, cancellationToken);
        return serverStream;
    }
}
