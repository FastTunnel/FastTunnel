// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.MiddleWare;

public class FastTunnelClientHandler
{
    private readonly ILogger<FastTunnelClientHandler> logger;
    private readonly FastTunnelServer fastTunnelServer;
    private readonly Version serverVersion;
    private readonly ILoginHandler loginHandler;

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
            if (!context.WebSockets.IsWebSocketRequest || !context.Request.Headers.TryGetValue(ProtocolConst.FASTTUNNEL_VERSION, out var version))
            {
                await next();
                return;
            };

            await handleClient(context, version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理登录异常");
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

        var client = new TunnelClient(webSocket, fastTunnelServer, loginHandler, context.Connection.RemoteIpAddress);
        client.ConnectionPort = context.Connection.LocalPort;

        try
        {
            fastTunnelServer.OnClientLogin(client);
            await client.ReviceAsync(CancellationToken.None);

            fastTunnelServer.OnClientLogout(client);
        }
        catch (Exception)
        {
            fastTunnelServer.OnClientLogout(client);
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
        if (!context.Request.Headers.TryGetValue(ProtocolConst.FASTTUNNEL_TOKEN, out var token))
            return false;

        if (fastTunnelServer.ServerOption.CurrentValue.Tokens?.Contains(token) ?? false)
            return true;

        return false;
    }
}
