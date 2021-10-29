using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.MiddleWares;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.MiddleWare
{
    public class FastTunnelSwapHandler
    {
        ILogger<FastTunnelClientHandler> logger;
        FastTunnelServer fastTunnelServer;

        public FastTunnelSwapHandler(ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
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
                await next();
                return;
            }

            using var reverseConnection = new WebSocketStream(lifetime, transport);
            responseAwaiter.TrySetResult(reverseConnection);

            var closedAwaiter = new TaskCompletionSource<object>();
            closedAwaiter.SetTimeOut(1000 * 60 * 30, null);

            lifetime.ConnectionClosed.Register((task) =>
            {
                (task as TaskCompletionSource<object>).SetResult(null);
            }, closedAwaiter);

            await closedAwaiter.Task;
            logger.LogError($"[PROXY]:Closed {requestId}");
        }
    }
}
