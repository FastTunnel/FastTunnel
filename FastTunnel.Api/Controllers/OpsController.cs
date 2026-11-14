// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Linq;
using FastTunnel.Api.Models;
using FastTunnel.Core.Config;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FastTunnel.Api.Controllers
{
    /// <summary>
    /// 运维最小集。<c>/healthz</c> 与 <c>/metrics</c> 走服务端自身的 HTTP 管道，
    /// 不经过隧道转发（请求 Host 未命中 WebList 时会落回 MVC）。
    ///
    /// 注意：若某个已映射域名正好叫 healthz/metrics 的宿主，那条请求会被转发到内网，
    /// 探活应使用服务端自身地址 + 端口，或由网关按路径固定路由到本端口。
    /// </summary>
    [ApiController]
    public class OpsController : ControllerBase
    {
        private readonly FastTunnelServer server;
        private readonly TrafficMeter meter;
        private readonly ClientTokenDirectory directory;
        private readonly IOptionsMonitor<DefaultServerConfig> options;

        public OpsController(
            FastTunnelServer server, TrafficMeter meter, ClientTokenDirectory directory,
            IOptionsMonitor<DefaultServerConfig> options)
        {
            this.server = server;
            this.meter = meter;
            this.directory = directory;
            this.options = options;
        }

        /// <summary>存活与就绪概览。匿名开放，不含凭据明文。</summary>
        [AllowAnonymous]
        [HttpGet("/healthz")]
        public IActionResult Health()
        {
            if (!PublicAllowed())
            {
                return Unauthorized(new { error = "Ops:PublicEndpoints=false，请携带 JWT" });
            }

            var traffic = meter.Snapshot();
            var stats = BuildStats();

            return Ok(new
            {
                status = "ok",
                uptimeSeconds = Math.Round(traffic.UptimeSeconds, 3),
                version = stats.Version,
                clientsOnline = stats.ClientsOnline,
                webMappings = stats.WebMappings,
                forwardMappings = stats.ForwardMappings,
                activeConnections = traffic.ActiveConnections,
                pendingSwaps = stats.PendingSwaps,
                proxyCount = traffic.Proxies.Count,
                requestsTotal = traffic.RequestsTotal,
                rejected = new { banned = traffic.RejectedBanned, allowlist = traffic.RejectedAllowlist },
                bytes = new { inbound = traffic.TotalBytesIn, outbound = traffic.TotalBytesOut },
                credentials = new
                {
                    named = stats.ConfiguredCredentials,
                    legacy = stats.LegacyCredentials,
                    warnings = stats.Warnings,
                },
            });
        }

        /// <summary>Prometheus 0.0.4 文本格式。累计 counter 的速率由抓取端用 rate() 计算。</summary>
        [AllowAnonymous]
        [HttpGet("/metrics")]
        public IActionResult Metrics()
        {
            if (!PublicAllowed())
            {
                return Unauthorized(new { error = "Ops:PublicEndpoints=false，请携带 JWT" });
            }

            var text = PrometheusText.Render(
                meter.Snapshot(), BuildStats(), options.CurrentValue.Ops.IncludeClientLabels);

            return Content(text, "text/plain; version=0.0.4; charset=utf-8");
        }

        /// <summary>按代理的流量明细（JSON）。需要 JWT，含主体 Id 与配置告警。</summary>
        [Authorize]
        [HttpGet("/api/Ops/Traffic")]
        public IActionResult Traffic()
        {
            var traffic = meter.Snapshot();
            var response = new ApiResponse();
            response.data = new
            {
                traffic.UptimeSeconds,
                traffic.TotalBytesIn,
                traffic.TotalBytesOut,
                traffic.RequestsTotal,
                traffic.RejectedBanned,
                traffic.RejectedAllowlist,
                traffic.ActiveConnections,
                traffic.ThrottleDelaySeconds,
                Proxies = traffic.Proxies.Select(x => new
                {
                    x.Proxy,
                    x.Client,
                    x.BytesIn,
                    x.BytesOut,
                    x.BytesTotal,
                    x.ConnectionsTotal,
                    x.ActiveConnections,
                    x.ThrottleDelaySeconds,
                    x.LimitBytesPerSecond,
                    x.FirstSeenUtc,
                }).ToArray(),
            };

            return Ok(response);
        }

        private bool PublicAllowed()
            => options.CurrentValue.Ops.PublicEndpoints || (User?.Identity?.IsAuthenticated ?? false);

        private ServerStats BuildStats() => new()
        {
            ClientsOnline = server.ConnectedClientCount,
            WebMappings = server.WebList.Count,
            ForwardMappings = server.ForwardList.Count,
            PendingSwaps = server.ResponseTasks.Count,
            Version = typeof(FastTunnelServer).Assembly.GetName().Version?.ToString() ?? "unknown",
            ConfiguredCredentials = directory.Credentials.Count,
            LegacyCredentials = directory.LegacyTokens.Count,
            Warnings = directory.Diagnostics,
        };
    }
}
