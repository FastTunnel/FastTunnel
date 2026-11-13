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
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Server;

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

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var log = loggerFactory.CreateLogger<TunnelClient>();
        var client = new TunnelClient(webSocket, fastTunnelServer, loginHandler, context.Connection.RemoteIpAddress, log);
        client.ConnectionPort = context.Connection.LocalPort;

        var loggedIn = false;
        try
        {
            fastTunnelServer.OnClientLogin(client);
            loggedIn = true;

            using var clientCts = new CancellationTokenSource();
            var receiving = client.ReviceAsync(clientCts.Token);
            var monitoring = client.MonitorAsync(clientCts.Token);

            // 接收循环或心跳看门狗任一结束，都认为该客户端离线。
            await Task.WhenAny(receiving, monitoring);

            clientCts.Cancel();
            await AwaitQuietlyAsync(receiving, monitoring);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "客户端连接异常结束 {RemoteIp}", context.Connection.RemoteIpAddress);
        }
        finally
        {
            if (loggedIn)
            {
                fastTunnelServer.OnClientLogout(client);
            }
        }
    }

    private static async Task AwaitQuietlyAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // 连接断开时两条任务都会抛异常，这里统一吞掉
        }
    }

    private static async Task Close(WebSocket webSocket, string reason)
    {
        await webSocket.SendMessageAsync(MessageType.Log, new LogMessage { Text = reason, Level = "error" }, CancellationToken.None);
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
