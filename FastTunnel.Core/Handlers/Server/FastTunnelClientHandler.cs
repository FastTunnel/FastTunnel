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
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Mux;
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
            if (!context.WebSockets.IsWebSocketRequest
                || !context.Request.Headers.TryGetValue(ProtocolConst.FASTTUNNEL_VERSION, out var version))
            {
                await next();
                return;
            };

            // /mux/{muxId}：多路复用数据通道的 WebSocket 承载。
            // 与 TCP 版承载的是同一套 MUX 帧，只是用 HTTP Upgrade 穿防火墙 / 反代。
            if (context.Request.Path.StartsWithSegments(ProtocolConst.MUX_PATH, out var remaining))
            {
                await handleMux(context, remaining);
                return;
            }

            await handleClient(context, version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理隧道客户端连接异常");
        }
    }

    /// <summary>
    /// WebSocket 承载的多路复用数据通道。
    ///
    /// 为什么要有它：TCP 承载要求客户端能直连服务端端口，遇到只放行 HTTP(S) 的网络
    /// （企业出口防火墙、只暴露 443 的反代）就没法用。WebSocket 是一次标准 HTTP Upgrade，
    /// 握手之后承载的仍是同一套 <see cref="MuxSession"/> 帧，能力与 TCP 版完全一致。
    /// </summary>
    private async Task handleMux(HttpContext context, PathString remaining)
    {
        if (!fastTunnelServer.ServerOption.CurrentValue.EnableMux)
        {
            logger.LogWarning("收到 WebSocket 多路复用连接，但服务端未启用多路复用（FastTunnel:EnableMux=false）");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!checkToken(context))
        {
            logger.LogWarning("WebSocket 多路复用连接 Token 校验失败 {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!Guid.TryParse(remaining.Value?.Trim('/'), out var muxId))
        {
            logger.LogWarning("WebSocket 多路复用连接缺少合法的会话标识：{Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        // MUX 连接与 LogIn 消息可能并发到达，给一个短暂等待窗口
        var client = await fastTunnelServer.WaitMuxClientAsync(
            muxId, TimeSpan.FromSeconds(5), context.RequestAborted);

        if (client == null)
        {
            logger.LogWarning("未知的 MUX 会话 {MuxId}（WebSocket），拒绝建立多路复用连接", muxId);
            await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unknown session", CancellationToken.None);
            return;
        }

        await using var duplex = new WebSocketDuplexPipe(webSocket);
        var session = new MuxSession(duplex, isServer: true, logger);
        client.AttachMux(session);
        logger.LogInformation("客户端 {RemoteIp} 建立多路复用连接（WebSocket）", client.RemoteIpAddress);

        try
        {
            await session.RunAsync();
        }
        finally
        {
            client.DetachMux(session);
            logger.LogInformation("客户端 {RemoteIp} 多路复用连接已关闭（WebSocket）", client.RemoteIpAddress);
            await session.DisposeAsync();
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
