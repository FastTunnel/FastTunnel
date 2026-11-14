// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.Forwarder.Kestrel.Features;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

/// <summary>
/// 公网访客请求的准入闸门，注册在 <c>InitializerMiddleware</c> 之后、<c>ForwarderMiddleware</c> 之前。
/// 因此它拦在 <c>waitSwap</c> 分叉成 mux / 回连两条路径**之前**，一次判定天然覆盖两条数据面，
/// 不会出现「只封了一条路径」的漏洞。
///
/// 判定只依赖已解析出来的 <see cref="IFastTunnelFeature"/>，不消费 Pipe 里的任何字节：
/// 请求头仍完整留给后面的 HTTP 解析器（沿用 §6.2 记过的 AdvanceTo(Start, Start) 约束）。
/// </summary>
internal class GuardMiddleware
{
    private readonly ConnectionDelegate next;
    private readonly ILogger<GuardMiddleware> logger;
    private readonly FastTunnelServer fastTunnelServer;
    private readonly BanRegistry bans;
    private readonly AuditLog? audit;
    private readonly TrafficMeter? meter;

    public GuardMiddleware(
        ConnectionDelegate next, ILogger<GuardMiddleware> logger, FastTunnelServer fastTunnelServer,
        BanRegistry bans, AuditLog? audit = null, TrafficMeter? meter = null)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
        this.bans = bans;
        this.audit = audit;
        this.meter = meter;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var feat = context.Features.Get<IFastTunnelFeature>();
        if (feat == null)
        {
            // 不是隧道请求（含 Host 未命中的普通 HTTP），交给 Kestrel
            await next(context);
            return;
        }

        // 控制面 / 数据面承载连接不受访客白名单与熔断影响，它们靠 token 与登录准入管控
        if (feat.Method == ProtocolConst.HTTP_METHOD_SWAP || feat.Method == ProtocolConst.HTTP_METHOD_MUX)
        {
            await next(context);
            return;
        }

        meter?.RecordRequest();

        // 一户一 token 后 account 才有可靠来源；hostBanned 时映射已被摘除，仍靠 host 命中。
        var hit = bans.Match(feat.Host, feat.MatchWeb?.Client?.Token, feat.MatchWeb?.Client?.Subject);
        if (hit != null)
        {
            meter?.RecordRejection(true);
            logger.LogWarning("命中封禁 {BanId}（{Type}/{Level}），拒绝 {ClientIp} 访问 {Host}{Path}",
                hit.Id, hit.SubjectType, hit.Level, feat.ClientIp, feat.Host, feat.Path);

            WriteAudit("blocked_request", feat, hit.Id, hit.GroupId, hit.Level.ToString());
            await WriteRejectionAsync(context, "451 Unavailable For Legal Reasons",
                "451 Unavailable For Legal Reasons: access has been suspended");
            return;
        }

        var options = fastTunnelServer.ServerOption.CurrentValue;
        if (!ClientIpResolver.IsAllowed(feat.ClientIp, options.WebAllowAccessIps))
        {
            meter?.RecordRejection(false);
            logger.LogWarning("访问来源 {ClientIp} 不在白名单内，拒绝 {Host}{Path}",
                feat.ClientIp, feat.Host, feat.Path);

            WriteAudit("denied_by_allowlist", feat, null, null, null);
            await WriteRejectionAsync(context, "403 Forbidden", "403 Forbidden: client address is not allowed");
            return;
        }

        await next(context);
    }

    private void WriteAudit(string action, IFastTunnelFeature feat, string? banId, string? groupId, string? level)
    {
        audit?.Write(new AuditEntry
        {
            Action = action,
            ClientIp = feat.ClientIp?.ToString(),
            Host = feat.Host,
            Path = feat.Path,
            Method = feat.Method,
            BanId = banId,
            GroupId = groupId,
            Level = level,
        });
    }

    /// <summary>
    /// 在连接层直接回一个最小 HTTP 响应并关闭连接。此时请求头还完整留在 Pipe 里没被消费，
    /// Kestrel 的 HTTP 解析器尚未接手，所以不能用 ASP.NET Core 的 HttpContext / StatusCode。
    /// TlsMiddleware 解密过的连接会由它自动把明文响应加密回去。
    /// 响应体不暴露命中规则与内部原因，避免泄露检测逻辑。
    /// </summary>
    private static async Task WriteRejectionAsync(ConnectionContext context, string statusLine, string body)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {statusLine}\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
            + "Connection: close\r\n\r\n"
            + body);

        try
        {
            await context.Transport.Output.WriteAsync(payload);
        }
        finally
        {
            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
        }
    }
}
