// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Client;

/// <summary>
/// 客户端侧的多路复用连接：登录成功后额外建立一条长连接，
/// 之后服务端每来一条公网连接，只需要在这条连接上开一条流。
///
/// 若服务端不支持（版本较旧、或配置里关掉了 EnableMux），连接会被立刻关闭，
/// 这里判定为不支持并放弃重试，业务自动回退到旧的回连模式。
/// </summary>
public sealed class MuxClient
{
    /// <summary>建立后存活不足该时长就断开，视为「服务端不支持」。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>空闲保活间隔，避免 NAT 静默回收这条长连接。</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly FastTunnelClient _client;
    private readonly SwapHandler _swapHandler;
    private readonly ILogger<MuxClient> _logger;

    public MuxClient(FastTunnelClient client, SwapHandler swapHandler, ILogger<MuxClient> logger)
    {
        _client = client;
        _swapHandler = swapHandler;
        _logger = logger;
    }

    /// <summary>本轮控制连接生命周期内运行，控制面断开即停止。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = Environment.TickCount64;
            bool established;

            try
            {
                established = await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (established && Environment.TickCount64 - startedAt < ProbeWindow.TotalMilliseconds)
            {
                // 握手成功却几乎立刻被断开：服务端不认识 MUX，别再徒劳重试
                _logger.LogInformation("[Mux]服务端未启用多路复用，回退到回连模式");
                return;
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 建立一次多路复用连接并跑完它的生命周期。
    /// 返回 true 表示握手已完成（能被断开说明服务端认识 MUX）。
    /// </summary>
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        Socket socket;
        SocketDuplexPipe duplex;

        try
        {
            (socket, duplex) = await OpenSessionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 连不上/写不出去属于网络问题，与服务端是否支持多路复用无关
            _logger.LogDebug(ex, "[Mux]建立连接失败");
            return false;
        }

        try
        {
            var session = new MuxSession(duplex, isServer: false, _logger,
                (stream, request) => OnAcceptAsync(stream, request, cancellationToken));

            // 保活任务随会话一起结束，不能 await——它是无限循环，会拖住探测判定
            _ = KeepAliveLoopAsync(session, cancellationToken);
            await session.RunAsync();
        }
        finally
        {
            await duplex.DisposeAsync();
            socket.Dispose();
        }

        return true;
    }

    private async Task<(Socket Socket, SocketDuplexPipe Duplex)> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var server = _client.Server;

        // 不指定 AddressFamily，让运行时按平台选择（通常是 IPv6 双栈，v4 地址也能连）
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(server.ServerAddr, server.ServerPort, cancellationToken);
            _logger.LogInformation("建立多路复用连接 {Addr}:{Port}", server.ServerAddr, server.ServerPort);

            var duplex = new SocketDuplexPipe(socket);

            // 握手沿用与 PROXY 一致的形式，服务端在连接层按首行方法名分派
            var handshake = $"MUX /{_client.MuxId} HTTP/1.1\r\nHost: {server.ServerAddr}:{server.ServerPort}\r\n\r\n";
            await duplex.Output.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);
            await duplex.Output.FlushAsync(cancellationToken);

            return (socket, duplex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task KeepAliveLoopAsync(MuxSession session, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await session.SendPingAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]保活发送失败");
        }
    }

    /// <summary>服务端开了一条新流：连内网目标，然后把两侧对接起来。</summary>
    private async Task OnAcceptAsync(MuxStream stream, SwapRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("[Mux]新流 {StreamId} => {Host}:{Port} ({Proto})",
            stream.Id, request.Host, request.Port, request.Proto);

        try
        {
            await _swapHandler.BridgeAsync(request, stream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]流 {StreamId} 转发失败", stream.Id);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }
}
