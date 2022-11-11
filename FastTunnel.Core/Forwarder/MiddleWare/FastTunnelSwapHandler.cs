using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.MiddleWare
{
    public class FastTunnelSwapHandler
    {
        ILogger<FastTunnelClientHandler> logger;
        FastTunnelServer fastTunnelServer;
        static int connectionCount;

        public static int ConnectionCount => connectionCount;

        public FastTunnelSwapHandler(ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
        {
            Interlocked.Increment(ref connectionCount);

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
                responseAwaiter.Item1.TrySetResult(reverseConnection);

                CancellationTokenSource cts;
                if (responseAwaiter.Item2 != CancellationToken.None)
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ConnectionClosed, responseAwaiter.Item2);
                }
                else
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ConnectionClosed);
                }

                var closedAwaiter = new TaskCompletionSource<object>();

                //lifetime.ConnectionClosed.Register((task) =>
                //{
                //    (task as TaskCompletionSource<object>).SetResult(null);
                //}, closedAwaiter);

                await closedAwaiter.Task.WaitAsync(cts.Token);
                logger.LogDebug($"[PROXY]:Closed {requestId}");
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex);
            }
            finally
            {
                Interlocked.Decrement(ref connectionCount);
                logger.LogDebug($"统计SWAP连接数：{ConnectionCount}");
            }
        }
    }
}
