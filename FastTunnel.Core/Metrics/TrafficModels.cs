// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using FastTunnel.Core.Models;

namespace FastTunnel.Core.Metrics;

/// <summary>相对公网访客的方向：In = 访客上传到内网，Out = 内网下发给访客。</summary>
public enum TrafficDirection
{
    In,
    Out,
}

/// <summary>
/// 一次转发的计量归属。Proxy 是聚合键：HTTP 用完整主机名，端口转发用 <c>port:&lt;RemotePort&gt;</c>；
/// Client 是一户一 token 的主体 Id，未认证时为 anonymous。
/// </summary>
public readonly struct TrafficScope
{
    public TrafficScope(string proxy, string? client, long? clientLimit = null)
    {
        Proxy = string.IsNullOrWhiteSpace(proxy) ? "unknown" : proxy.ToLowerInvariant();
        Client = string.IsNullOrWhiteSpace(client) ? "anonymous" : client;
        ClientLimit = clientLimit;
    }

    public string Proxy { get; }

    public string Client { get; }

    /// <summary>Clients[].BandwidthLimit；null 表示继承全局 / 按主体规则。</summary>
    public long? ClientLimit { get; }

    public static TrafficScope ForWeb(WebInfo web)
        => new(web.Host, web.Client?.Subject, web.Client?.Credential?.BandwidthLimit);

    public static TrafficScope ForPort(int remotePort, TunnelClient? client)
        => new($"port:{remotePort}", client?.Subject, client?.Credential?.BandwidthLimit);

    public static TrafficScope Anonymous(string proxy) => new(proxy, null);
}

public sealed class ProxyTrafficSnapshot
{
    public string Proxy { get; set; } = string.Empty;

    public string Client { get; set; } = "anonymous";

    public long BytesIn { get; set; }

    public long BytesOut { get; set; }

    public long BytesTotal => BytesIn + BytesOut;

    public long ConnectionsTotal { get; set; }

    public long ActiveConnections { get; set; }

    public double ThrottleDelaySeconds { get; set; }

    /// <summary>当前生效的速率上限（字节/秒）；0 表示不限速。</summary>
    public long LimitBytesPerSecond { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }
}

public sealed class TrafficSnapshot
{
    public double UptimeSeconds { get; set; }

    public long TotalBytesIn { get; set; }

    public long TotalBytesOut { get; set; }

    public long RequestsTotal { get; set; }

    public long RejectedBanned { get; set; }

    public long RejectedAllowlist { get; set; }

    public long ActiveConnections { get; set; }

    public double ThrottleDelaySeconds { get; set; }

    public IReadOnlyList<ProxyTrafficSnapshot> Proxies { get; set; } = Array.Empty<ProxyTrafficSnapshot>();
}

/// <summary>/healthz 与 /metrics 用到的服务端状态，由 OpsController 组装。</summary>
public sealed class ServerStats
{
    public int ClientsOnline { get; set; }

    public int WebMappings { get; set; }

    public int ForwardMappings { get; set; }

    public int PendingSwaps { get; set; }

    public string Version { get; set; } = "unknown";

    public int ConfiguredCredentials { get; set; }

    public int LegacyCredentials { get; set; }

    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}
