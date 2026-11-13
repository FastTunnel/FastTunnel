// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Text.Json;
using FastTunnel.Core.Protocol;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 控制面消息。今天把 payload 从竖线文本换成 JSON，最关键的要求是「新旧双向兼容」：
/// 新服务端还得能读懂老客户端，老客户端收到新服务端下发的消息也不能崩。
/// </summary>
public class ControlProtocolTests
{
    [Theory]
    [InlineData("{\"id\":\"a\"}", true)]
    [InlineData("{}", true)]
    [InlineData("id|127.0.0.1:80", false)]
    [InlineData("", false)]
    public void IsStructured_按首字符判断(string payload, bool expected)
        => Assert.Equal(expected, ControlProtocol.IsStructured(payload));

    [Fact]
    public void ParseSwapRequest_解析JSON()
    {
        var request = ControlProtocol.ParseSwapRequest(
            "{\"id\":\"abc\",\"host\":\"10.0.0.8\",\"port\":8080,\"proto\":\"tcp\"}");

        Assert.Equal("abc", request.Id);
        Assert.Equal("10.0.0.8", request.Host);
        Assert.Equal(8080, request.Port);
        Assert.Equal("tcp", request.Proto);
    }

    [Fact]
    public void ParseSwapRequest_JSON缺id被拒绝()
    {
        var ex = Assert.Throws<FormatException>(
            () => ControlProtocol.ParseSwapRequest("{\"host\":\"10.0.0.8\",\"port\":8080}"));

        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void ParseSwapRequest_兼容旧版TCP文本()
    {
        var request = ControlProtocol.ParseSwapRequest("4f2b0e3c-0000-0000-0000-000000000001|10.0.0.8:8080");

        Assert.Equal("4f2b0e3c-0000-0000-0000-000000000001", request.Id);
        Assert.Equal("10.0.0.8", request.Host);
        Assert.Equal(8080, request.Port);
        Assert.Equal("tcp", request.Proto);
    }

    [Fact]
    public void ParseSwapRequest_兼容旧版UDP文本()
    {
        var request = ControlProtocol.ParseSwapRequest("id-1|udp|10.0.0.8:9999");

        Assert.Equal("id-1", request.Id);
        Assert.Equal("udp", request.Proto);
        Assert.Equal(9999, request.Port);
    }

    [Fact]
    public void ParseSwapRequest_兼容旧版IPv6方括号()
    {
        // 旧版客户端对 IPv6 目标会写 [::1]:9999，按 ':' 硬切会切坏
        var request = ControlProtocol.ParseSwapRequest("id-2|udp|[::1]:9999");

        Assert.Equal("::1", request.Host);
        Assert.Equal(9999, request.Port);
        Assert.Equal("udp", request.Proto);
    }

    [Theory]
    [InlineData("")]
    [InlineData("only-one-part")]
    [InlineData("id|no-port-here")]
    public void ParseSwapRequest_畸形文本抛格式异常(string payload)
        => Assert.Throws<FormatException>(() => ControlProtocol.ParseSwapRequest(payload));

    [Fact]
    public void ToLegacyPayload_降级为旧协议文本()
    {
        var tcp = new SwapRequest { Id = "a", Host = "10.0.0.8", Port = 8080, Proto = "tcp" };
        var udp = new SwapRequest { Id = "b", Host = "10.0.0.8", Port = 9999, Proto = "udp" };

        Assert.Equal("a|10.0.0.8:8080", ControlProtocol.ToLegacyPayload(tcp));
        Assert.Equal("b|udp|10.0.0.8:9999", ControlProtocol.ToLegacyPayload(udp));
    }

    [Fact]
    public void ToLegacyPayload_IPv6目标加方括号()
    {
        var request = new SwapRequest { Id = "c", Host = "::1", Port = 9999, Proto = "udp" };

        Assert.Equal("c|udp|[::1]:9999", ControlProtocol.ToLegacyPayload(request));
    }

    [Fact]
    public void FormatAddress_IPv6加方括号_域名不加()
    {
        Assert.Equal("[::1]:9999", ControlProtocol.FormatAddress(new SwapRequest { Host = "::1", Port = 9999 }));
        Assert.Equal("test.test.cc:80", ControlProtocol.FormatAddress(new SwapRequest { Host = "test.test.cc", Port = 80 }));
    }

    [Fact]
    public void 新旧格式可以互转_不丢字段()
    {
        var original = new SwapRequest { Id = "roundtrip", Host = "::1", Port = 8443, Proto = "tcp" };

        // 新 → 旧 → 新
        var fromLegacy = ControlProtocol.ParseSwapRequest(ControlProtocol.ToLegacyPayload(original));

        Assert.Equal(original.Id, fromLegacy.Id);
        Assert.Equal(original.Host, fromLegacy.Host);
        Assert.Equal(original.Port, fromLegacy.Port);
        Assert.Equal(original.Proto, fromLegacy.Proto);
    }

    [Fact]
    public void JSON序列化使用camelCase且忽略null()
    {
        var json = JsonSerializer.Serialize(new SwapRequest { Id = "x", Host = "h", Port = 1 }, ControlProtocol.JsonOptions);

        Assert.Contains("\"id\"", json);
        Assert.Contains("\"host\"", json);
        Assert.Contains("\"port\"", json);
        Assert.DoesNotContain("Proto", json);
    }

    [Fact]
    public void 心跳消息往返()
    {
        var original = new HeartbeatMessage { SentAt = 123456789 };

        var json = JsonSerializer.Serialize(original, ControlProtocol.JsonOptions);
        var restored = JsonSerializer.Deserialize<HeartbeatMessage>(json, ControlProtocol.JsonOptions)!;

        Assert.Equal(original.SentAt, restored.SentAt);
    }

    [Fact]
    public void 协议版本号区分新旧客户端()
    {
        Assert.Equal(2, ControlProtocol.Version);
        Assert.Equal(1, ControlProtocol.LegacyVersion);
        Assert.True(ControlProtocol.Version > ControlProtocol.LegacyVersion);
    }
}
