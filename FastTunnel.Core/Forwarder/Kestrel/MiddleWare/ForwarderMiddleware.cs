// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.Forwarder.Kestrel.Features;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using FastTunnel.Core.Utilitys;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
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
        var requestId = Guid.NewGuid().ToString().Replace("-", "");

        Interlocked.Increment(ref UserCount);

        logger.LogDebug($"=========USER START {requestId}===========");
        var web = feat.MatchWeb;

        TaskCompletionSource<(Stream, CancellationTokenSource)> tcs = new();
        logger.LogDebug($"[Http]Swap开始 {requestId}|{feat.Host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");

        if (!fastTunnelServer.ResponseTasks.TryAdd(requestId, tcs))
        {
            return;
        }

        (Stream Stream, CancellationTokenSource TokenSource) res = (null, null);

        try
        {
            var ss = context.LocalEndPoint;

            try
            {
                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{requestId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", context.ConnectionClosed);
            }
            catch (WebSocketException)
            {
                web.LogOut();

                // 通讯异常，返回客户端离线
                throw new ClienOffLineException("客户端离线");
            }

            res = await tcs.Task;

            var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(res.TokenSource.Token, context.ConnectionClosed);
            using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);

            //var t1 = res.Input.CopyToAsync(context.Transport.Output, context.ConnectionClosed);
            //var t2 = context.Transport.Input.CopyToAsync(res.Output, context.ConnectionClosed);
            var t1 = res.Stream.CopyToAsync(reverseConnection, tokenSource.Token);
            var t2 = reverseConnection.CopyToAsync(res.Stream, tokenSource.Token);

            await Task.WhenAny(t1, t2).WaitAsync(tokenSource.Token);
        }
        catch (Exception)
        {
        }
        finally
        {
            Interlocked.Decrement(ref UserCount);
            logger.LogDebug($"=========USER END {requestId}===========");
            fastTunnelServer.ResponseTasks.TryRemove(requestId, out _);

            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();

            res.TokenSource?.Cancel();
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

        CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed);

        using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseStream.TrySetResult((reverseConnection, cancellationTokenSource));

        var closedAwaiter = new TaskCompletionSource<object>();

        cancellationTokenSource.Token.Register(() =>
        {
            closedAwaiter.TrySetCanceled();
        });

        try
        {
            await closedAwaiter.Task;
        }
        catch (Exception)
        {
        }
        finally
        {
            Interlocked.Decrement(ref ClientCount);
            logger.LogDebug($"=========CLINET END {requestId}===========");
            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
        }
    }
}
