// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.Kestrel.MiddleWare;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Mux;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using FastTunnel.Core.Sockets;
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
    private readonly BanRegistry? bans;
    private readonly ClientTokenDirectory? directory;
    private readonly PeerRegistry? registry;
    private readonly PeerBroker peers = new();

    public FastTunnelClientHandler(
        ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer,
        ILoginHandler loginHandler, BanRegistry? bans = null, ClientTokenDirectory? directory = null,
        PeerRegistry? registry = null)
    {
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
        this.loginHandler = loginHandler;
        this.bans = bans;
        this.directory = directory;
        this.registry = registry;

        serverVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    }

    public async Task Handle(HttpContext context, Func<Task> next)
    {
        try
        {
            if (context.Request.Path == PeerProtocol.Path && context.WebSockets.IsWebSocketRequest
                && context.Request.Headers.ContainsKey(ProtocolConst.FASTTUNNEL_VERSION))
            {
                if (!fastTunnelServer.ServerOption.CurrentValue.EnableP2P)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (!context.Request.IsHttps && context.Features.Get<TunnelTlsFeature>() == null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var supplied = context.Request.Headers[ProtocolConst.FASTTUNNEL_TOKEN].ToString();
                PeerIdentity? identity = null;
                if (PeerInvites.IsInvite(supplied))
                {
                    var key = fastTunnelServer.ServerOption.CurrentValue.P2PInviteKey;
                    if (PeerInvites.Verify(supplied, key, DateTimeOffset.UtcNow) is { } invite)
                        identity = new PeerIdentity(invite.Id, supplied, invite);
                }
                else if (checkToken(context, out _, out var credential) && credential is { IsLegacy: false })
                {
                    identity = new PeerIdentity(credential.Id, credential.Token);
                }

                if (identity == null || !IsPeerAuthorized(identity))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await peers.RunAsync(context, identity, IsPeerAuthorized);
                return;
            }

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

        if (!checkToken(context, out _, out _))
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

        if (!checkToken(context, out var token, out var credential))
        {
            await Close(webSocket, "Token验证失败");
            return;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var log = loggerFactory.CreateLogger<TunnelClient>();

        // 双栈监听下 IPv4 客户端显示为 ::ffff:x.x.x.x，还原成正常写法便于排查
        var remoteIp = NetUtil.Unmap(context.Connection.RemoteIpAddress) ?? IPAddress.None;
        var client = new TunnelClient(webSocket, fastTunnelServer, loginHandler, remoteIp, log);
        client.ConnectionPort = context.Connection.LocalPort;

        // 把命中的凭据与主体挂到会话上：封禁 / 审计 / 带宽统计都按 Subject 归属，
        // 而不是靠可被多人共用的 token 明文反查。
        client.Token = token;
        client.Credential = credential;

        var loggedIn = false;
        try
        {
            var enforce = fastTunnelServer.ServerOption.CurrentValue.EnforceSingleSession;
            var displaced = fastTunnelServer.OnClientLogin(client, enforce);
            loggedIn = true;

            if (displaced != null)
            {
                logger.LogWarning("主体 {Subject} 重复登录：断开旧会话 {OldIp}，保留新会话 {NewIp}",
                    client.Subject, displaced.RemoteIpAddress, client.RemoteIpAddress);
                fastTunnelServer.TerminateClient(displaced, "同主体重复登录");
            }

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

    private bool IsPeerAuthorized(PeerIdentity identity)
    {
        var options = fastTunnelServer.ServerOption.CurrentValue;
        if (!options.EnableP2P || bans?.IsTokenBanned(identity.Token) == true || bans?.IsAccountBanned(identity.Id) == true)
            return false;
        if (identity.Invite is { } invite)
            return PeerInvites.Verify(identity.Token, options.P2PInviteKey, DateTimeOffset.UtcNow) == invite;
        return directory?.TryFind(identity.Token, out var current) == true
            && current is { IsLegacy: false }
            && string.Equals(current.Id, identity.Id, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// 控制面 token 校验。优先走一户一 token 的主体目录；未注入目录时（例如直接构造的测试替身）
    /// 回退到旧版共享 Tokens 校验。封禁判定在两处承载上共用。
    /// </summary>
    private bool checkToken(HttpContext context, out string? matched, out ClientCredential? credential)
    {
        matched = null;
        credential = null;
        if (PeerInvites.IsInvite(context.Request.Headers[ProtocolConst.FASTTUNNEL_TOKEN].ToString()))
            return false;

        if (directory != null)
        {
            if (!directory.TryResolve(context, out credential))
            {
                return false;
            }

            matched = credential?.Token;
        }
        else if (!ClientTokenValidator.TryValidate(
                     context, fastTunnelServer.ServerOption.CurrentValue.Tokens, out matched))
        {
            return false;
        }

        // 封禁的凭据不能重新接入：控制面与 WebSocket MUX 承载共用这一处校验
        if (matched != null && bans?.IsTokenBanned(matched) == true)
        {
            logger.LogWarning("已被封禁的凭据尝试接入 {RemoteIp}", context.Connection.RemoteIpAddress);
            matched = null;
            credential = null;
            return false;
        }

        // 入网设备的最后上线时间只在内存里累加，随台账下一次结构性变更一起落盘
        registry?.Touch(credential?.Id);
        return true;
    }
}
