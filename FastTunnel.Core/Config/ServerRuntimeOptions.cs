// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace FastTunnel.Core.Config;

/// <summary>带宽限速规则。所有速率单位都是「字节/秒」，0 表示不限速。</summary>
public sealed class BandwidthOptions
{
    /// <summary>全局默认上限。0 表示不限速（默认）。</summary>
    public long DefaultBytesPerSecond { get; set; }

    /// <summary>
    /// 令牌桶容量 = 速率 × 该秒数。允许短时突发，默认 1 秒。
    /// 过小会让大响应被切成很多细碎等待，过大则起不到限速作用。
    /// </summary>
    public double BurstSeconds { get; set; } = 1.0;

    /// <summary>按代理覆盖：键是完整主机名，或端口转发用的 <c>port:&lt;RemotePort&gt;</c>。</summary>
    public Dictionary<string, long> PerProxy { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按主体覆盖：键是 <c>Clients[].Id</c>。</summary>
    public Dictionary<string, long> PerClient { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>运维端点行为。</summary>
public sealed class OpsOptions
{
    /// <summary>
    /// <c>/healthz</c> 与 <c>/metrics</c> 是否匿名开放（Prometheus / 负载均衡探活通常无凭据）。
    /// 设为 false 时两者要求 JWT；也建议在网关层限制来源 IP。
    /// </summary>
    public bool PublicEndpoints { get; set; } = true;

    /// <summary>
    /// <c>/metrics</c> 是否输出 <c>client</c> 标签（主体 Id）。
    /// 关闭后只保留按代理聚合的数据，避免把账号名单暴露给匿名抓取端。
    /// </summary>
    public bool IncludeClientLabels { get; set; } = true;
}
