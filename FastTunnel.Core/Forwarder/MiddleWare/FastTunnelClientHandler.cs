using FastTunnel.Core.Client;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.MiddleWares
{
    public class FastTunnelClientHandler
    {
        ILogger<FastTunnelClientHandler> logger;
        FastTunnelServer fastTunnelServer;

        public FastTunnelClientHandler(ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
        {
            if (!context.WebSockets.IsWebSocketRequest
                || !context.Request.Headers.TryGetValue(HeaderConst.FASTTUNNEL_FLAG, out var version)
                || !context.Request.Headers.TryGetValue(HeaderConst.FASTTUNNEL_TYPE, out var type))
            {
                await next();
                return;
            };

            if (HeaderConst.TYPE_CLIENT.Equals(type))
            {
                await Client(context, next);
            }
            else if (HeaderConst.TYPE_SWAP.Equals(type))
            {
                await Swap(context, next);
            }
            else
            {
                logger.LogError($"参数异常，ConnectionType类型为{type}");
            }
        }

        private async Task Swap(HttpContext context, Func<Task> next)
        {
            var requestId = context.Request.Path.Value.Trim('/');
            if (!fastTunnelServer.ResponseTasks.TryGetValue(requestId, out var response))
            {
                logger.LogError($"requestId不存在:{requestId}");
                return;
            };

            var lifetime = context.Features.Get<IConnectionLifetimeFeature>();
            var transport = context.Features.Get<IConnectionTransportFeature>();

            if (lifetime == null || transport == null)
            {
                await next();
                return;
            }

            using var stream = new WebSocketStream(lifetime, transport);
            response.TrySetResult(stream);

            logger.LogInformation($"Swap Set {requestId}");

            var closedAwaiter = new TaskCompletionSource();
            lifetime.ConnectionClosed.Register((task) => { (task as TaskCompletionSource).SetResult(); }, closedAwaiter);

            await closedAwaiter.Task;

            logger.LogInformation($"Swap Completion {requestId}");
        }

        private async Task Client(HttpContext context, Func<Task> next)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new TunnelClient(logger, webSocket, fastTunnelServer);

            this.logger.LogInformation($"{client} 客户端连接成功");

            try
            {
                await client.ReviceAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通信异常");
            }

            this.logger.LogInformation($"{client} 客户端断开连接");
        }
    }
}
