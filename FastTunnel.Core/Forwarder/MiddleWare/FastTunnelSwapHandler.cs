// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.MiddleWare;

public class FastTunnelSwapHandler
{
    private readonly ILogger<FastTunnelClientHandler> logger;
    private readonly FastTunnelServer fastTunnelServer;

    public FastTunnelSwapHandler(ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer)
    {
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    public async Task Handle(HttpContext context, Func<Task> next)
    {
        try
        {
            if (context.Request.Method != "PROXY")
            {
                await next();
                return;
            }

            var requestId = context.Request.Path.Value.Trim('/');
            logger.LogDebug($"[PROXY]:Start {requestId}");

            if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseAwaiter))
            {
                logger.LogError($"[PROXY]:RequestId不存在 {requestId}");
                return;
            };

            var lifetime = context.Features.Get<IConnectionLifetimeFeature>();
            var transport = context.Features.Get<IConnectionTransportFeature>();

            if (lifetime == null || transport == null)
            {
                return;
            }

            using var reverseConnection = new WebSocketStream(lifetime, transport);
            responseAwaiter.TrySetResult(reverseConnection);

            var closedAwaiter = new TaskCompletionSource<object>();

            lifetime.ConnectionClosed.Register((task) =>
            {
                (task as TaskCompletionSource<object>).SetResult(null);
            }, closedAwaiter);

            await closedAwaiter.Task;
            logger.LogDebug($"[PROXY]:Closed {requestId}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex);
        }
    }
}
