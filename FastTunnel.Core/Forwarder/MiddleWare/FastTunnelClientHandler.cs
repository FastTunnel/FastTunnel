// Copyright (c) 2019-2022 Gui.H. https://github.com/FastTunnel/FastTunnel
// The FastTunnel licenses this file to you under the Apache License Version 2.0.
// For more details,You may obtain License file at: https://github.com/FastTunnel/FastTunnel/blob/v2/LICENSE

using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.MiddleWare
{
    public class FastTunnelClientHandler
    {
        readonly ILogger<FastTunnelClientHandler> logger;
        readonly FastTunnelServer fastTunnelServer;
        readonly Version serverVersion;
        readonly ILoginHandler loginHandler;

        static int connectionCount;

        public static int ConnectionCount => connectionCount;

        public FastTunnelClientHandler(
            ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer, ILoginHandler loginHandler)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
            this.loginHandler = loginHandler;

            serverVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
        {
            try
            {
                if (!context.WebSockets.IsWebSocketRequest || !context.Request.Headers.TryGetValue(FastTunnelConst.FASTTUNNEL_VERSION, out var version))
                {
                    await next();
                    return;
                };

                Interlocked.Increment(ref connectionCount);

                try
                {
                    await handleClient(context, version);
                }
                finally
                {
                    Interlocked.Decrement(ref connectionCount);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex);
            }
        }

        private async Task handleClient(HttpContext context, string clientVersion)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

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

            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var log = loggerFactory.CreateLogger<TunnelClient>();
            var client = new TunnelClient(webSocket, fastTunnelServer, loginHandler, context.Connection.RemoteIpAddress, log);
            client.ConnectionPort = context.Connection.LocalPort;

            try
            {
                fastTunnelServer.ClientLogin(client);
                await client.ReviceAsync(context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "客户端异常");
            }
            finally
            {
                fastTunnelServer.ClientLogout(client);
            }
        }

        private static async Task Close(WebSocket webSocket, string reason)
        {
            await webSocket.SendCmdAsync(MessageType.Log, reason, CancellationToken.None);
            await webSocket.CloseAsync(WebSocketCloseStatus.Empty, string.Empty, CancellationToken.None);
            return;
        }

        private bool checkToken(HttpContext context)
        {
            var checkToken = false;
            if (fastTunnelServer.ServerOption.CurrentValue.Tokens != null && fastTunnelServer.ServerOption.CurrentValue.Tokens.Count != 0)
            {
                checkToken = true;
            }

            if (!checkToken)
                return true;

            // 客户端未携带token，登录失败
            if (!context.Request.Headers.TryGetValue(FastTunnelConst.FASTTUNNEL_TOKEN, out var token))
                return false;

            if (fastTunnelServer.ServerOption.CurrentValue.Tokens?.Contains(token) ?? false)
                return true;

            return false;
        }
    }
}
