// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Server;

public class ForwardDispatcher
{
    private readonly FastTunnelServer _server;
    private readonly ForwardConfig _config;
    private readonly WebSocket _client;
    private readonly ILogger logger;

    public ForwardDispatcher(ILogger logger, FastTunnelServer server, ForwardConfig config, WebSocket client)
    {
        this.logger = logger;
        _server = server;
        _config = config;
        _client = client;
    }

    int SwapCount;

    /// <summary>
    /// 申请一条与 FastTunnel 客户端之间的 swap 隧道流。
    /// 用于 UDP 端口转发等需要直接持有底层流的场景。
    /// </summary>
    /// <param name="protocol">"tcp" 或 "udp"</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<(Stream Stream, CancellationTokenSource TokenSource)> RequestSwapAsync(
        string protocol, CancellationToken cancellationToken)
    {
        var msgId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<(Stream Stream, CancellationTokenSource Token)>();
        if (!_server.ResponseTasks.TryAdd(msgId, tcs))
        {
            throw new InvalidOperationException("无法申请 swap 通道");
        }

        try
        {
            var payload = string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase)
                ? $"{msgId}|udp|{_config.LocalIp}:{_config.LocalPort}"
                : $"{msgId}|{_config.LocalIp}:{_config.LocalPort}";

            await _client.SendCmdAsync(MessageType.Forward, payload, cancellationToken);
        }
        catch
        {
            _server.ResponseTasks.TryRemove(msgId, out _);
            throw;
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch
        {
            _server.ResponseTasks.TryRemove(msgId, out _);
            throw;
        }
    }

    /// <summary>
    /// 处理 TCP 端口转发请求
    /// </summary>
    /// <param name="_socket">用户请求</param>
    /// <returns></returns>
    public async Task DispatchAsync(Socket _socket)
    {
        var msgId = Guid.NewGuid();

        (Stream Stream, CancellationTokenSource TokenSource) res = default;

        Interlocked.Increment(ref SwapCount);

        try
        {
            logger.LogDebug($"[Forward]Swap开始 {msgId}|{_config.RemotePort}=>{_config.LocalIp}:{_config.LocalPort}");
            var tcs = new TaskCompletionSource<(Stream Stream, CancellationTokenSource TokenSource)>();
            if (!_server.ResponseTasks.TryAdd(msgId, tcs))
            {
                return;
            }

            try
            {
                await _client.SendCmdAsync(MessageType.Forward, $"{msgId}|{_config.LocalIp}:{_config.LocalPort}", CancellationToken.None);
            }
            catch (SocketClosedException sex)
            {
                // TODO:客户端已掉线，但是没有移除对端口的监听
                logger.LogError($"[Forward]Swap 客户端已离线 {sex.Message}");
                tcs.TrySetCanceled();
                Close(_socket);
                return;
            }
            catch (Exception ex)
            {
                // 网络不稳定
                logger.LogError(ex, $"[Forward]Swap Exception");
                tcs.TrySetCanceled();
                Close(_socket);
                return;
            }

            res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var stream2 = new NetworkStream(_socket);
            await Task.WhenAny(res.Stream.CopyToAsync(stream2), stream2.CopyToAsync(res.Stream)).WaitAsync(res.TokenSource.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug($"[Forward]Swap Error {msgId}：" + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref SwapCount);
            res.TokenSource?.Cancel();
            logger.LogDebug($"[Forward]Swap OK {msgId} {SwapCount}");
            _server.ResponseTasks.TryRemove(msgId, out _);
        }
    }

    private void Close(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
        }
        finally
        {
            socket.Close();
        }
    }
}
