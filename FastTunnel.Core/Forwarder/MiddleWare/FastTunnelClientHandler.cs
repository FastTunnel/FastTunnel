using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.MiddleWares
{
    public class FastTunnelClientHandler
    {
        ILogger<FastTunnelClientHandler> logger;
        FastTunnelServer fastTunnelServer;
        Version serverVersion;

        public FastTunnelClientHandler(ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;

            serverVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
        {
            if (!context.WebSockets.IsWebSocketRequest || !context.Request.Headers.TryGetValue(FastTunnelConst.FASTTUNNEL_VERSION, out var version))
            {
                await next();
                return;
            };

            await handleClient(context, next, version);
        }

        private async Task handleClient(HttpContext context, Func<Task> next, string clientVersion)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var tunnelClient = context.RequestServices.GetRequiredService<TunnelClient>().SetSocket(webSocket);

            if (Version.Parse(clientVersion).Major != serverVersion.Major)
            {
                await Close(webSocket, $"客户端版本{clientVersion}与服务端版本{serverVersion}不兼容，请升级。");
                return;
            }

            if (!checkToken(context))
            {
                await Close(webSocket, "Token验证失败");
                return;
            }

            try
            {
                Interlocked.Increment(ref fastTunnelServer.ConnectedClientCount);
                logger.LogInformation($"客户端连接 {context.TraceIdentifier}:{context.Connection.RemoteIpAddress} 当前在线数：{fastTunnelServer.ConnectedClientCount}");
                await tunnelClient.ReviceAsync(CancellationToken.None);

                logOut(context);
            }
            catch (Exception)
            {
                logOut(context);
            }
        }

        private void logOut(HttpContext context)
        {
            Interlocked.Decrement(ref fastTunnelServer.ConnectedClientCount);
            logger.LogInformation($"客户端关闭 {context.TraceIdentifier}:{context.Connection.RemoteIpAddress} 当前在线数：{fastTunnelServer.ConnectedClientCount}");
        }

        private static async Task Close(WebSocket webSocket, string reason)
        {
            await webSocket.SendCmdAsync(MessageType.Log, reason, CancellationToken.None);
            await webSocket.CloseAsync(WebSocketCloseStatus.Empty, string.Empty, CancellationToken.None);
            return;
        }

        private bool checkToken(HttpContext context)
        {
            if (string.IsNullOrEmpty(fastTunnelServer.ServerOption.CurrentValue.Token))
            {
                return true;
            }

            if (!context.Request.Headers.TryGetValue(FastTunnelConst.FASTTUNNEL_TOKEN, out var token) || !token.Equals(fastTunnelServer.ServerOption.CurrentValue.Token))
            {
                return false;
            };

            return true;
        }
    }
}
