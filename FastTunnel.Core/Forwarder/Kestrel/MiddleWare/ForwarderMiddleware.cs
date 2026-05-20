// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.Kestrel.Features;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Refs;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

/// <summary>
/// 核心逻辑处理中间件
/// </summary>
internal class ForwarderMiddleware
{
    private readonly ConnectionDelegate next;
    private readonly ILogger<ForwarderMiddleware> logger;
    private readonly FastTunnelServer fastTunnelServer;

    public ForwarderMiddleware(ConnectionDelegate next, ILogger<ForwarderMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var feat = context.Features.Get<IFastTunnelFeature>();
        if (feat == null)
        {
            // not fasttunnel request
            await next(context);
            return;
        }
        else
        {
            if (feat.Method == ProtocolConst.HTTP_METHOD_SWAP)
            {
                await doSwap(context);
            }
            else if (feat.MatchWeb != null)
            {
                await waitSwap(context);
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }

    public int UserCount = 0;
    public int ClientCount = 0;

    /// <summary>
    /// 用户向服务端发起的请求
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    private async Task waitSwap(ConnectionContext context)
    {
        var feat = context.Features.Get<IFastTunnelFeature>();
        var requestId = Guid.NewGuid();

        Interlocked.Increment(ref UserCount);

        logger.LogDebug($"=========USER START {requestId}===========");
        var web = feat.MatchWeb;

        // RunContinuationsAsynchronously 避免 doSwap 的线程被 waitSwap 的后续工作占用，
        // 防止高并发下同一客户端的多个请求被内联序列化。
        var tcs = new TaskCompletionSource<(Stream, CancellationTokenSource)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        logger.LogDebug($"[Http]Swap开始 {requestId}|{feat.Host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");

        if (!fastTunnelServer.ResponseTasks.TryAdd(requestId, tcs))
        {
            return;
        }

        (Stream Stream, CancellationTokenSource TokenSource) res = (null, null);

        try
        {
            try
            {
                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{requestId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", context.ConnectionClosed);
            }
            catch (WebSocketException)
            {
                web.LogOut();
                throw new ClienOffLineException("客户端离线");
            }
            catch (SocketClosedException)
            {
                // 控制平面 WebSocket 已关闭，同样识别为客户端离线
                web.LogOut();
                throw new ClienOffLineException("客户端离线");
            }

            // 关键 Bug 修复：给 await 加上取消令牌，避免客户端不回包时永久挂起，
            // 造成 ResponseTasks 与连接资源泄漏。
            res = await tcs.Task.WaitAsync(context.ConnectionClosed);

            using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                res.TokenSource.Token, context.ConnectionClosed);

            // 零拷贝快路径：swap 侧与 user 侧都是 Pipe，直接使用 PipeReader.CopyToAsync(PipeWriter)，
            // 避免 Stream.CopyToAsync 默认 81920 字节中转缓冲区与额外的 Span 拷贝。
            Task t1, t2;
            if (res.Stream is DuplexPipeStream dps)
            {
                t1 = dps.Input.CopyToAsync(context.Transport.Output, tokenSource.Token);
                t2 = context.Transport.Input.CopyToAsync(dps.Output, tokenSource.Token);
            }
            else
            {
                // 兼容路径：若对端不是 DuplexPipeStream。
                using var reverseConnection = new DuplexPipeStream(
                    context.Transport.Input, context.Transport.Output, true);
                t1 = res.Stream.CopyToAsync(reverseConnection, tokenSource.Token);
                t2 = reverseConnection.CopyToAsync(res.Stream, tokenSource.Token);
                try
                {
                    await Task.WhenAny(t1, t2);
                }
                finally
                {
                    tokenSource.Cancel();
                }
                return;
            }

            try
            {
                // 任一方向结束后立即取消另一方向，不再等到 finally 才传递取消。
                await Task.WhenAny(t1, t2);
            }
            finally
            {
                tokenSource.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // 用户连接关闭/超时取消，正常路径
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, $"[Http]Swap 异常 {requestId}");
        }
        finally
        {
            Interlocked.Decrement(ref UserCount);
            logger.LogDebug($"=========USER END {requestId} {UserCount}===========");
            fastTunnelServer.ResponseTasks.TryRemove(requestId, out _);

            // 先取消另一侧（唤醒 doSwap），再完成本侧管道，避免 doSwap 端在未取消时错误使用已关闭资源。
            res.TokenSource?.Cancel();

            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
        }
    }

    /// <summary>
    /// 内网向服务端发起的请求
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task doSwap(ConnectionContext context)
    {
        Interlocked.Increment(ref ClientCount);
        var feat = context.Features.Get<IFastTunnelFeature>();
        var requestId = feat.MessageId;

        logger.LogDebug($"=========CLINET START {requestId}===========");

        if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseStream))
        {
            throw new Exception($"[PROXY]:RequestId不存在 {requestId}");
        };

        // 修正：该链接 CTS 原本从未被释放，每个请求都会在 ConnectionClosed 上遗留一个注册。
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed);

        using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseStream.TrySetResult((reverseConnection, cancellationTokenSource));

        try
        {
            // 等待 waitSwap 完成后的取消信号。原先使用一个从未 SetResult 的 TaskCompletionSource，
            // 这里直接用 Task.Delay(Infinite, token) 更简洁，避免多余的 TCS 分配。
            await Task.Delay(Timeout.Infinite, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, $"[PROXY]doSwap 异常 {requestId}");
        }
        finally
        {
            Interlocked.Decrement(ref ClientCount);
            logger.LogDebug($"=========CLINET END {requestId} {ClientCount}===========");
            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
        }
    }
}
