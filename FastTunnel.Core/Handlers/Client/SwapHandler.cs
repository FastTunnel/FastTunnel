// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Sockets;
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
            var msgs = msg.Split('|');
            requestId = msgs[0];

            // 兼容两种协议：
            //   TCP: {msgId}|{ip}:{port}
            //   UDP: {msgId}|udp|{ip}:{port}
            bool isUdp = msgs.Length >= 3 && string.Equals(msgs[1], "udp", StringComparison.OrdinalIgnoreCase);
            var address = isUdp ? msgs[2] : msgs[1];

            _logger.LogDebug($"========Swap Start:{requestId} ({(isUdp ? "UDP" : "TCP")})==========");

            using var serverStream = await createRemote(requestId, cleint, cancellationToken);

            if (isUdp)
            {
                await BridgeUdpAsync(address, serverStream, cancellationToken);
            }
            else
            {
                using var localStream = await createLocal(requestId, address, cancellationToken);

                var taskX = serverStream.CopyToAsync(localStream, cancellationToken);
                var taskY = localStream.CopyToAsync(serverStream, cancellationToken);

                await Task.WhenAny(taskX, taskY).WaitAsync(cancellationToken);
            }
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
    /// UDP 桥接：服务端的 swap 流 ↔ 内网 UDP 端点
    /// 数据帧格式：[2 字节大端长度][数据]
    /// </summary>
    private async Task BridgeUdpAsync(string address, Stream serverStream, CancellationToken cancellationToken)
    {
        var parts = address.Split(":");
        var host = parts[0];
        var port = int.Parse(parts[1]);

        IPAddress ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            var entries = await Dns.GetHostAddressesAsync(host, cancellationToken);
            ip = entries.Length > 0 ? entries[0] : throw new Exception($"无法解析 {host}");
        }

        var localTarget = new IPEndPoint(ip, port);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastActive = DateTime.UtcNow;

        async Task TunnelToLocalAsync()
        {
            var header = new byte[2];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(serverStream, header, 0, 2, cts.Token))
                        break;

                    int len = BinaryPrimitives.ReadUInt16BigEndian(header);
                    var buffer = len == 0 ? Array.Empty<byte>() : new byte[len];
                    if (len > 0 && !await ReadExactAsync(serverStream, buffer, 0, len, cts.Token))
                        break;

                    lastActive = DateTime.UtcNow;
                    try
                    {
                        await udp.SendAsync(buffer, len, localTarget);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[UdpSwap]send to local error");
                    }
                }
            }
            finally
            {
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

                    var frame = new byte[2 + data.Length];
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)data.Length);
                    if (data.Length > 0)
                    {
                        Buffer.BlockCopy(data, 0, frame, 2, data.Length);
                    }

                    lastActive = DateTime.UtcNow;
                    await serverStream.WriteAsync(frame, 0, frame.Length, cts.Token);
                    await serverStream.FlushAsync(cts.Token);
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

    private async Task<Stream> createLocal(string requestId, string localhost, CancellationToken cancellationToken)
    {
        var socket = await DnsSocketFactory.ConnectAsync(localhost.Split(":")[0], int.Parse(localhost.Split(":")[1]));
        return new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };
    }

    private async Task<Stream> createRemote(string requestId, FastTunnelClient cleint, CancellationToken cancellationToken)
    {
        var socket = await DnsSocketFactory.ConnectAsync(cleint.Server.ServerAddr, cleint.Server.ServerPort);
        Stream serverStream = new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };

        if (cleint.Server.Protocol == "wss")
        {
            var sslStream = new SslStream(serverStream, false, delegate { return true; });
            await sslStream.AuthenticateAsClientAsync(cleint.Server.ServerAddr);
            serverStream = sslStream;
        }

        var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";

        var requestMsg = Encoding.UTF8.GetBytes(reverse);
        await serverStream.WriteAsync(requestMsg, cancellationToken);
        return serverStream;
    }
}
