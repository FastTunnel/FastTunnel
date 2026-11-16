// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Server;

public class LoginHandler : ILoginHandler
{
    private readonly ILogger logger;
    private readonly BanRegistry? bans;
    public const bool NeedRecive = true;

    public LoginHandler(ILogger<LoginHandler> logger, BanRegistry? bans = null)
    {
        this.logger = logger;
        this.bans = bans;
    }

    protected async Task HandleLoginAsync(FastTunnelServer server, TunnelClient client, LogInMassage requet, CancellationToken cancellationToken)
    {
        var hasTunnel = false;

        await SendLogAsync(client.webSocket, "穿透协议 | 映射关系（公网=>内网）", cancellationToken);

        if (requet.Webs != null && requet.Webs.Any())
        {
            hasTunnel = await RegisterWebsAsync(server, client, requet.Webs, cancellationToken);
        }

        if (requet.Forwards != null && requet.Forwards.Any())
        {
            if (server.ServerOption.CurrentValue.EnableForward)
            {
                hasTunnel = true;
                var options = server.ServerOption.CurrentValue;

                foreach (var item in requet.Forwards)
                {
                    try
                    {
                        // 端口准入：白名单 + 每客户端端口数上限，拒绝只跳过该条并回日志
                        if (!PortPolicy.IsPortAllowed(options.AllowPorts, item.RemotePort, out var portReason))
                        {
                            logger.LogWarning("拒绝端口转发 {Protocol} {RemotePort}：{Reason}（客户端 {RemoteIp}）",
                                item.Protocol, item.RemotePort, portReason, client.RemoteIpAddress);
                            await SendLogAsync(client.webSocket,
                                $"  {item.Protocol,-6} | 端口 {item.RemotePort} 未登记：{portReason}", CancellationToken.None);
                            continue;
                        }

                        var maxPorts = options.MaxPortsPerClient;
                        if (maxPorts > 0 && client.CountForwardPorts(item.RemotePort) >= maxPorts)
                        {
                            logger.LogWarning("主体 {Subject} 转发端口数达到上限 {MaxPorts}，拒绝 {RemotePort}",
                                client.Subject, maxPorts, item.RemotePort);
                            await SendLogAsync(client.webSocket,
                                $"  {item.Protocol,-6} | 端口 {item.RemotePort} 未登记：转发端口数已达上限 {maxPorts}", CancellationToken.None);
                            continue;
                        }

                        if (server.ForwardList.TryGetValue(item.RemotePort, out var old))
                        {
                            logger.LogDebug($"Remove Listener {old.Listener.ListenIp}:{old.Listener.ListenPort}");
                            old.Listener.Stop();
                            server.RemoveForward(old);
                        }

                        var dispatcher = new ForwardDispatcher(logger, server, item, client);

                        IPortListener ls;
                        if (item.Protocol == ProtocolEnum.UDP)
                        {
                            ls = new UdpProxyListener("0.0.0.0", item.RemotePort, logger, dispatcher);
                        }
                        else
                        {
                            ls = new PortProxyListener("0.0.0.0", item.RemotePort, logger, dispatcher);
                        }

                        // 监听会在 TunnelClient.Logout() 里统一停止并摘除路由记录
                        ls.Start();

                        var forwardInfo = new ForwardInfo<ForwardHandlerArg> { Listener = ls, Socket = client.webSocket, SSHConfig = item };

                        server.ForwardList.TryAdd(item.RemotePort, forwardInfo);
                        logger.LogDebug($"{item.Protocol} proxy success: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");

                        client.AddForward(forwardInfo);
                        var protoLabel = item.Protocol == ProtocolEnum.UDP ? "UDP" : "TCP";
                        await SendLogAsync(client.webSocket, $"  {protoLabel,-6} | {server.ServerOption.CurrentValue.WebDomain}:{item.RemotePort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"{item.Protocol} proxy error: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                        logger.LogError(ex.Message);
                        await SendLogAsync(client.webSocket, ex.Message, CancellationToken.None);
                        continue;
                    }
                }
            }
            else
            {
                await SendLogAsync(client.webSocket, TunnelResource.ForwardDisabled, CancellationToken.None);
            }
        }

        if (!hasTunnel)
            await SendLogAsync(client.webSocket, TunnelResource.NoTunnel, CancellationToken.None);
    }

    public virtual async Task<bool> HandlerMsg(FastTunnelServer fastTunnelServer, TunnelClient tunnelClient, string lineCmd, CancellationToken cancellationToken)
    {
        var msg = JsonSerializer.Deserialize<LogInMassage>(lineCmd, AppJsonContext.Default.Options);
        if (msg == null)
        {
            throw new FormatException("LogIn 消息解析失败");
        }

        // 低于结构化版本的客户端仍按旧版竖线文本下发，避免升级服务端即断服。
        tunnelClient.ProtocolVersion = msg.ProtocolVersion >= ControlProtocol.Version
            ? msg.ProtocolVersion
            : ControlProtocol.LegacyVersion;

        // 登记多路复用会话标识，客户端随后会用同一个 id 建立 MUX 连接
        tunnelClient.MuxId = msg.MuxId;
        fastTunnelServer.RegisterMuxClient(tunnelClient);

        await HandleLoginAsync(fastTunnelServer, tunnelClient, msg, cancellationToken);
        return NeedRecive;
    }

    /// <summary>
    /// 登记子域名与自定义域名映射。任何一条域名校验不通过只跳过该条，不影响同一客户端的其它隧道。
    /// </summary>
    /// <returns>是否至少成功登记了一条映射。</returns>
    private async Task<bool> RegisterWebsAsync(
        FastTunnelServer server, TunnelClient client, IEnumerable<WebConfig> webs, CancellationToken cancellationToken)
    {
        var options = server.ServerOption.CurrentValue;
        if (!HostNameValidator.TryNormalize(options.WebDomain, out var rootDomain))
        {
            logger.LogWarning("服务端 WebDomain 未配置或非法，拒绝登记全部 Web 映射：{WebDomain}", options.WebDomain);
            await SendLogAsync(client.webSocket, "  HTTP   | 服务端 WebDomain 未配置或非法，已跳过全部 Web 映射", CancellationToken.None);
            return false;
        }

        var registered = false;
        foreach (var item in webs)
        {
            if (item == null)
            {
                continue;
            }

            if (!HostNameValidator.TryNormalizeSubDomain(item.SubDomain, out var subDomain))
            {
                logger.LogWarning("拒绝非法子域名 {SubDomain}（客户端 {RemoteIp}）", item.SubDomain, client.RemoteIpAddress);
                await SendLogAsync(client.webSocket, $"  HTTP   | 子域名非法，已跳过：{item.SubDomain}", CancellationToken.None);
                continue;
            }

            // 一户一 token：主体可限定只能使用自己的 SubDomain，避免一个用户批量抢注子域
            if (client.Credential?.IsSubDomainAllowed(subDomain) == false)
            {
                logger.LogWarning("主体 {Subject} 申请了未授权的 SubDomain {SubDomain}", client.Subject, subDomain);
                await SendLogAsync(client.webSocket, $"  HTTP   | 子域名未授权，已跳过：{subDomain}", CancellationToken.None);
                continue;
            }

            var hostName = $"{subDomain}.{rootDomain}";
            registered |= await TryLogMappingAsync(server, client, item, hostName, CancellationToken.None);

            if (item.WWW == null)
            {
                continue;
            }

            foreach (var raw in item.WWW)
            {
                if (!TryValidateCustomDomain(raw, rootDomain, options.WebDomainBlacklist, out var customHost, out var reason))
                {
                    logger.LogWarning("拒绝自定义域名 {Host}：{Reason}（客户端 {RemoteIp}）", raw, reason, client.RemoteIpAddress);
                    await SendLogAsync(client.webSocket, $"  HTTP   | 自定义域名 {raw} 未登记：{reason}", CancellationToken.None);
                    continue;
                }

                registered |= await TryLogMappingAsync(server, client, item, customHost, CancellationToken.None);
            }
        }

        return registered;
    }

    /// <summary>登记一条映射并把结果回给客户端日志。</summary>
    private async Task<bool> TryLogMappingAsync(
        FastTunnelServer server, TunnelClient client, WebConfig item, string host, CancellationToken cancellationToken)
    {
        if (!TryAddMapping(server, client, item, host, out var reason))
        {
            logger.LogWarning("拒绝域名映射 {Host}：{Reason}（客户端 {RemoteIp}）", host, reason, client.RemoteIpAddress);
            await SendLogAsync(client.webSocket, $"  HTTP   | {host} 未登记：{reason}", cancellationToken);
            return false;
        }

        logger.LogDebug("New Http '{Host}'", host);
        await SendLogAsync(client.webSocket,
            $"  HTTP   | http://{host}:{client.ConnectionPort} => {item.LocalIp}:{item.LocalPort}", cancellationToken);
        return true;
    }

    /// <summary>
    /// 自定义域名先过语法、平台域边界和运营黑名单，再进路由表。
    /// DNS 所有权验证（TXT 挑战）与 ICP 备案查询不在这里做，见合规方案 S0。
    /// </summary>
    private static bool TryValidateCustomDomain(
        string raw, string rootDomain, string[]? blacklist, out string host, out string reason)
    {
        if (!HostNameValidator.TryNormalize(raw, out host))
        {
            reason = "域名格式非法（须为 ASCII/punycode 域名，不能带协议、端口、路径或通配符）";
            return false;
        }

        if (HostNameValidator.IsPlatformDomain(host, rootDomain))
        {
            reason = "属于平台自有域名，请用 SubDomain 申请子域";
            return false;
        }

        if (HostNameValidator.IsBlacklisted(host, blacklist))
        {
            reason = "命中运营域名黑名单";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>每个域名一个独立的 WebInfo，下线时才能按 (host, 实例) 精确摘除。</summary>
    private bool TryAddMapping(
        FastTunnelServer server, TunnelClient client, WebConfig item, string host, out string reason)
    {
        // 封禁期间不允许用新会话重新登记同一域名，否则熔断会被一次重连绕过
        if (bans?.IsHostBanned(host) == true)
        {
            reason = "域名已被封禁，禁止重新登记";
            return false;
        }

        // 一户一 token：具名主体只能登记白名单内的域名（子域名与自定义域名一视同仁）
        if (client.Credential?.IsHostAllowed(host) == false)
        {
            reason = $"域名不在主体 {client.Subject} 的授权范围内";
            return false;
        }

        var info = new WebInfo
        {
            Host = host,
            Socket = client.webSocket,
            WebConfig = item,
            ProtocolVersion = client.ProtocolVersion,
            Client = client,
        };

        if (!server.TryRegisterWeb(host, info, out var registerReason))
        {
            reason = registerReason;
            return false;
        }

        client.AddWeb(info);
        reason = string.Empty;
        return true;
    }

    private static Task SendLogAsync(WebSocket socket, string text, CancellationToken cancellationToken)
        => socket.SendMessageAsync(MessageType.Log, new LogMessage { Text = text }, cancellationToken);
}
