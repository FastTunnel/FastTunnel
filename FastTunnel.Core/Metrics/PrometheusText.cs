// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Globalization;
using System.Text;

namespace FastTunnel.Core.Metrics;

/// <summary>
/// Prometheus 文本格式（0.0.4）渲染。不引第三方依赖，指标都是累计 counter / 瞬时 gauge，
/// 速率由 Prometheus 侧用 <c>rate()</c> 计算，所以这里不额外暴露「当前带宽」这种易误解的瞬时值。
/// </summary>
public static class PrometheusText
{
    public static string Render(TrafficSnapshot traffic, ServerStats server, bool includeClientLabels)
    {
        var sb = new StringBuilder(1024);
        NumberFormatInfo n = CultureInfo.InvariantCulture.NumberFormat;

        Gauge(sb, "fasttunnel_up", "服务端是否可达（能抓到即为 1）", 1, n);
        Gauge(sb, "fasttunnel_uptime_seconds", "进程启动至今秒数", traffic.UptimeSeconds, n);
        Info(sb, "fasttunnel_build_info", "构建信息", $"version=\"{Label(server.Version)}\"", 1, n);

        Gauge(sb, "fasttunnel_clients_online", "在线隧道客户端数", server.ClientsOnline, n);
        Gauge(sb, "fasttunnel_web_mappings", "已登记的 HTTP 域名映射数", server.WebMappings, n);
        Gauge(sb, "fasttunnel_forward_mappings", "已登记的端口转发映射数", server.ForwardMappings, n);
        Gauge(sb, "fasttunnel_pending_swaps", "等待客户端回连的请求数", server.PendingSwaps, n);
        Gauge(sb, "fasttunnel_configured_credentials", "一户一 token 中已配置的主体数", server.ConfiguredCredentials, n);
        Gauge(sb, "fasttunnel_legacy_credentials", "仍在使用的旧版共享凭据数", server.LegacyCredentials, n);

        Gauge(sb, "fasttunnel_active_connections", "当前活动转发连接数", traffic.ActiveConnections, n);
        Counter(sb, "fasttunnel_requests_total", "进入隧道的公网 HTTP 请求总数", null, traffic.RequestsTotal, n);
        Counter(sb, "fasttunnel_rejected_total", "被拒绝的请求总数", "reason=\"banned\"", traffic.RejectedBanned, n);
        Counter(sb, "fasttunnel_rejected_total", null, "reason=\"allowlist\"", traffic.RejectedAllowlist, n);
        Counter(sb, "fasttunnel_bytes_total", "转发字节总数", "direction=\"in\"", traffic.TotalBytesIn, n);
        Counter(sb, "fasttunnel_bytes_total", null, "direction=\"out\"", traffic.TotalBytesOut, n);
        Counter(sb, "fasttunnel_throttle_delay_seconds_total", "限速累计等待时长",
            null, traffic.ThrottleDelaySeconds, n);

        Header(sb, "fasttunnel_proxy_bytes_total", "按代理累计转发字节数", "counter");
        Header(sb, "fasttunnel_proxy_connections_total", "按代理累计转发连接数", "counter");
        Header(sb, "fasttunnel_proxy_active_connections", "按代理当前活动连接数", "gauge");
        Header(sb, "fasttunnel_proxy_throttle_delay_seconds_total", "按代理限速累计等待时长", "counter");
        Header(sb, "fasttunnel_proxy_bandwidth_limit_bytes_per_second",
            "按代理当前生效的速率上限（0 表示不限速）", "gauge");

        foreach (var proxy in traffic.Proxies)
        {
            var labels = Labels(proxy, includeClientLabels);
            sb.Append("fasttunnel_proxy_bytes_total{").Append(labels).Append(",direction=\"in\"} ")
              .Append(proxy.BytesIn.ToString(n)).Append('\n');
            sb.Append("fasttunnel_proxy_bytes_total{").Append(labels).Append(",direction=\"out\"} ")
              .Append(proxy.BytesOut.ToString(n)).Append('\n');

            sb.Append("fasttunnel_proxy_connections_total{").Append(labels).Append("} ")
              .Append(proxy.ConnectionsTotal.ToString(n)).Append('\n');
            sb.Append("fasttunnel_proxy_active_connections{").Append(labels).Append("} ")
              .Append(proxy.ActiveConnections.ToString(n)).Append('\n');
            sb.Append("fasttunnel_proxy_throttle_delay_seconds_total{").Append(labels).Append("} ")
              .Append(proxy.ThrottleDelaySeconds.ToString("F6", n)).Append('\n');
            sb.Append("fasttunnel_proxy_bandwidth_limit_bytes_per_second{").Append(labels).Append("} ")
              .Append(proxy.LimitBytesPerSecond.ToString(n)).Append('\n');
        }

        return sb.ToString();
    }

    private static string Labels(ProxyTrafficSnapshot proxy, bool includeClient)
        => includeClient
            ? $"proxy=\"{Label(proxy.Proxy)}\",client=\"{Label(proxy.Client)}\""
            : $"proxy=\"{Label(proxy.Proxy)}\"";

    private static void Gauge(StringBuilder sb, string name, string? help, double value, NumberFormatInfo n)
    {
        Header(sb, name, help, "gauge");
        sb.Append(name).Append(' ').Append(value.ToString("F3", n)).Append('\n');
    }

    private static void Counter(
        StringBuilder sb, string name, string? help, string? labels, double value, NumberFormatInfo n)
    {
        Header(sb, name, help, "counter");
        sb.Append(name);
        if (labels != null)
        {
            sb.Append('{').Append(labels).Append('}');
        }

        sb.Append(' ').Append(value.ToString(IsIntegral(value) ? "F0" : "F6", n)).Append('\n');
    }

    private static void Info(
        StringBuilder sb, string name, string help, string labels, int value, NumberFormatInfo n)
    {
        Header(sb, name, help, "gauge");
        sb.Append(name).Append('{').Append(labels).Append("} ").Append(value.ToString(n)).Append('\n');
    }

    private static void Header(StringBuilder sb, string name, string? help, string type)
    {
        if (help == null)
        {
            return;
        }

        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static bool IsIntegral(double value) => Math.Abs(value - Math.Round(value)) < 0.000001;

    /// <summary>Prometheus 标签值转义：反斜杠、双引号、换行。</summary>
    internal static string Label(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\"", "\\\"");
}
