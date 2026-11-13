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
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Mux;
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
            else if (feat.Method == ProtocolConst.HTTP_METHOD_MUX)
            {
                await doMux(context);
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
        var web = feat.MatchWeb;

        // 多路复用优先：一条公网连接 = 一条流，省掉「通知客户端回连」的整套往返。
        var mux = web.Client?.MuxSession;
        if (mux is { IsClosed: false })
        {
            await waitSwapViaMux(context, mux, web);
            return;
        }

        var requestId = Guid.NewGuid();

        Interlocked.Increment(ref UserCount);

        logger.LogDebug($"=========USER START {requestId}===========");

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
                var swap = new SwapRequest
                {
                    Id = requestId.ToString(),
                    Host = web.WebConfig.LocalIp,
                    Port = web.WebConfig.LocalPort,
                    Proto = "tcp",
                };

                // 按客户端能力选择格式，未升级的客户端继续收旧版竖线文本。
                if (web.ProtocolVersion >= ControlProtocol.Version)
                {
                    await web.Socket.SendMessageAsync(MessageType.SwapMsg, swap, context.ConnectionClosed);
                }
                else
                {
                    await web.Socket.SendCmdAsync(MessageType.SwapMsg, ControlProtocol.ToLegacyPayload(swap), context.ConnectionClosed);
                }
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
    /// 多路复用会话：客户端建立的一条长连接，之后所有 HTTP / TCP 转发都在它上面开流。
    /// 这条连接本身不承载任何 HTTP 语义，只是帧的载体。
    /// </summary>
    private async Task doMux(ConnectionContext context)
    {
        var feat = context.Features.Get<IFastTunnelFeature>();

        if (!fastTunnelServer.ServerOption.CurrentValue.EnableMux)
        {
            // 主动拒绝而不是抛异常：抛出去会被 Kestrel 记为 Unhandled，日志里全是噪声
            logger.LogWarning("收到 MUX 连接但服务端未启用多路复用，拒绝");
            await CloseQuietlyAsync(context);
            return;
        }

        // MUX 连接与 LogIn 消息可能并发到达，给一个短暂等待窗口
        var client = await fastTunnelServer.WaitMuxClientAsync(
            feat.MessageId, TimeSpan.FromSeconds(5), context.ConnectionClosed);

        if (client == null)
        {
            logger.LogWarning("未知的 MUX 会话 {MuxId}，拒绝建立多路复用连接", feat.MessageId);
            await CloseQuietlyAsync(context);
            return;
        }

        var session = new MuxSession(context.Transport, isServer: true, logger);
        client.AttachMux(session);
        logger.LogInformation("客户端 {RemoteIp} 建立多路复用连接", client.RemoteIpAddress);

        try
        {
            await session.RunAsync();
        }
        finally
        {
            client.DetachMux(session);
            logger.LogInformation("客户端 {RemoteIp} 多路复用连接已关闭", client.RemoteIpAddress);
            await session.DisposeAsync();
            await CloseQuietlyAsync(context);
        }
    }

    /// <summary>关闭连接且不抛出，避免 Kestrel 把正常拒绝记成未处理异常。</summary>
    private static async Task CloseQuietlyAsync(ConnectionContext context)
    {
        try
        {
            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
        }
        catch
        {
            // 连接可能已被对端关闭
        }
    }

    /// <summary>
    /// 多路复用版的 HTTP 转发：在会话上开一条流，SYN 里带上内网目标地址，
    /// 之后就是纯粹的字节管道对拷，无需控制面往返。
    /// </summary>
    private async Task waitSwapViaMux(ConnectionContext context, MuxSession mux, WebInfo web)
    {
        Interlocked.Increment(ref UserCount);

        MuxStream? stream = null;
        try
        {
            var request = new SwapRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Host = web.WebConfig.LocalIp,
                Port = web.WebConfig.LocalPort,
                Proto = "tcp",
            };

            stream = await mux.OpenStreamAsync(request, context.ConnectionClosed);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed, mux.Closed);

            // 两侧都是 Pipe，直接用 PipeReader.CopyToAsync(PipeWriter)，不经过 Stream 的中转缓冲。
            var response = stream.Input.CopyToAsync(context.Transport.Output, cts.Token);
            var request2 = context.Transport.Input.CopyToAsync(stream.Output, cts.Token);

            try
            {
                await Task.WhenAny(response, request2);
            }
            finally
            {
                cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // 用户连接关闭，正常路径
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Mux]HTTP 转发异常");
        }
        finally
        {
            Interlocked.Decrement(ref UserCount);

            // 优雅关闭：等已排队的数据发完再回收，否则响应体会被截断
            if (stream != null)
            {
                await stream.DisposeAsync();
            }

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
