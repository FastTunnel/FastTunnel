// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
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
        logger.LogInformation("=========ForwarderMiddleware SART===========");

        var feat = context.Features.Get<IFastTunnelFeature>();
        if (feat == null)
        {
            logger.LogInformation("=========ForwarderMiddleware END===========");
            // not fasttunnel request
            await next(context);
            return;
        }
        else
        {
            logger.LogInformation("=========Swap STRART===========");
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

            logger.LogInformation("=========Swap END===========");
            logger.LogInformation("=========ForwarderMiddleware END===========");
        }
    }

    private async Task waitSwap(ConnectionContext context)
    {
        var feat = context.Features.Get<IFastTunnelFeature>();
        var requestId = Guid.NewGuid().ToString().Replace("-", "");
        var web = feat.MatchWeb;

        TaskCompletionSource<Stream> tcs = new();
        logger.LogDebug($"[Http]Swap开始 {requestId}|{feat.Host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");
        tcs.SetTimeOut(10000, () => { logger.LogDebug($"[Proxy TimeOut]:{requestId}"); });

        fastTunnelServer.ResponseTasks.TryAdd(requestId, tcs);

        try
        {
            try
            {
                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{requestId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", default);
            }
            catch (WebSocketException)
            {
                web.LogOut();

                // 通讯异常，返回客户端离线
                throw new ClienOffLineException("客户端离线");
            }

            using var res = await tcs.Task;
            using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);

            var t1 = res.CopyToAsync(reverseConnection);
            var t2 = reverseConnection.CopyToAsync(res);

            await Task.WhenAll(t1, t2);

            logger.LogDebug("[Http]Swap结束");
        }
        catch (Exception)
        {
            throw;
        }
        finally
        {
            fastTunnelServer.ResponseTasks.TryRemove(requestId, out _);
            context.Transport.Input.Complete();
            context.Transport.Output.Complete();
        }
    }

    private async Task doSwap(ConnectionContext context)
    {
        var feat = context.Features.Get<IFastTunnelFeature>();
        var requestId = feat.MessageId;
        if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseStream))
        {
            throw new Exception($"[PROXY]:RequestId不存在 {requestId}");
        };

        using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseStream.TrySetResult(reverseConnection);

        var lifetime = context.Features.Get<IConnectionLifetimeFeature>();

        var closedAwaiter = new TaskCompletionSource<object>();

        lifetime.ConnectionClosed.Register((task) =>
        {
            (task as TaskCompletionSource<object>).SetResult(null);
        }, closedAwaiter);

        try
        {
            await closedAwaiter.Task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "");
        }
        finally
        {
            context.Transport.Input.Complete();
            context.Transport.Output.Complete();
            logger.LogInformation($"=====================Swap End:{requestId}================== ");
        }
    }
}
