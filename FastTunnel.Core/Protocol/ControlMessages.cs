// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTunnel.Core.Protocol;

/// <summary>
/// 控制面结构化协议。
/// 帧格式保持 [1 byte MessageType][payload][\n] 不变，只把 payload 从竖线分隔的裸文本
/// 升级为 JSON，使新增字段（多路复用 streamId、打洞信令、错误码等）不再破坏兼容性。
/// </summary>
public static class ControlProtocol
{
    /// <summary>结构化协议版本，写入 LogIn 供服务端决定下发格式。</summary>
    public const int Version = 2;

    /// <summary>遗留竖线文本协议版本，用于识别未升级的客户端。</summary>
    public const int LegacyVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>payload 是否为新版 JSON 结构。</summary>
    public static bool IsStructured(string payload)
        => payload.Length > 0 && payload[0] == '{';

    /// <summary>
    /// 解析 SwapMsg / Forward 的 payload，同时兼容新版 JSON 与旧版竖线文本。
    /// 旧协议：{id}|{host}:{port} 或 {id}|udp|{host}:{port}
    /// </summary>
    public static SwapRequest ParseSwapRequest(string payload)
    {
        if (IsStructured(payload))
        {
            var request = JsonSerializer.Deserialize<SwapRequest>(payload, JsonOptions)
                ?? throw new FormatException("SwapRequest 反序列化结果为空");
            if (string.IsNullOrEmpty(request.Id))
                throw new FormatException("SwapRequest 缺少 id");
            return request;
        }

        var parts = payload.Split('|');
        if (parts.Length < 2)
            throw new FormatException($"无法解析控制消息: {payload}");

        var isUdp = parts.Length >= 3 && string.Equals(parts[1], "udp", StringComparison.OrdinalIgnoreCase);
        var address = isUdp ? parts[2] : parts[1];
        var separator = address.LastIndexOf(':');
        if (separator <= 0)
            throw new FormatException($"无法解析目标地址: {address}");

        return new SwapRequest
        {
            Id = parts[0],
            Host = address[..separator].Trim('[', ']'),
            Port = int.Parse(address[(separator + 1)..]),
            Proto = isUdp ? "udp" : "tcp",
        };
    }

    /// <summary>把请求降级为旧版竖线文本，供未升级的客户端消费。</summary>
    public static string ToLegacyPayload(SwapRequest request)
        => string.Equals(request.Proto, "udp", StringComparison.OrdinalIgnoreCase)
            ? $"{request.Id}|udp|{FormatAddress(request)}"
            : $"{request.Id}|{FormatAddress(request)}";

    public static string FormatAddress(SwapRequest request)
        => request.Host.Contains(':') ? $"[{request.Host}]:{request.Port}" : $"{request.Host}:{request.Port}";
}

/// <summary>SwapMsg / Forward 的统一载荷。</summary>
public sealed class SwapRequest
{
    /// <summary>会话标识，与回连请求行中的 id 一一对应。</summary>
    public string Id { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>tcp / udp。</summary>
    public string Proto { get; set; } = "tcp";
}

/// <summary>Log 消息载荷。</summary>
public sealed class LogMessage
{
    public string Text { get; set; } = string.Empty;

    /// <summary>info / warn / error。</summary>
    public string Level { get; set; } = "info";
}

/// <summary>Ping / Pong 载荷，仅用于观测，不参与链路状态判定。</summary>
public sealed class HeartbeatMessage
{
    /// <summary>发送方的 Environment.TickCount64。</summary>
    public long SentAt { get; set; }
}
