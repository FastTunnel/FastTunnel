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
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.Forwarder.Kestrel.Features;
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

        TaskCompletionSource<IDuplexPipe> tcs = new();
        logger.LogDebug($"[Http]Swap开始 {requestId}|{feat.Host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");
        tcs.SetTimeOut(10000, () => { logger.LogDebug($"[Proxy TimeOut]:{requestId}"); });

        fastTunnelServer.ResponseTasks.TryAdd(requestId, tcs);

        IDuplexPipe res = null;

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

                // 通讯异常，返回客户端离线
                throw new ClienOffLineException("客户端离线");
            }
 
            res = await tcs.Task;

            //  using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);

            var t1 = res.Input.CopyToAsync(context.Transport.Output, context.ConnectionClosed);
            var t2 = context.Transport.Input.CopyToAsync(res.Output, context.ConnectionClosed);
            await Task.WhenAny(t1, t2);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[waitResponse]");
        }
        finally
        {
            logger.LogInformation("[Http] waitSwap结束");
            fastTunnelServer.ResponseTasks.TryRemove(requestId, out _);

            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();

            await res.Input.CompleteAsync();
            await res.Output.CompleteAsync();
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

        //using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseStream.TrySetResult(context.Transport);

        var closedAwaiter = new TaskCompletionSource<object>();

        try
        {
            closedAwaiter.Task.Wait(context.ConnectionClosed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[setResponse]");
        }
        finally
        {
            logger.LogInformation($"=====================Swap End:{requestId}================== ");
            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
            context.Abort();
        }
    }
}
