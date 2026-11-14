// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Server;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// TCP/UDP 转发的服务端端口准入规则：端口合法性、白名单命中、非法条目。
/// </summary>
public class PortPolicyTests
{
    [Fact]
    public void 未配置白名单时不限制()
    {
        Assert.True(PortPolicy.IsPortAllowed(null, 8080, out _));
        Assert.True(PortPolicy.IsPortAllowed(new List<AllowPortOptions>(), 8080, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void 端口超出合法范围一律拒绝(int port)
    {
        Assert.False(PortPolicy.IsPortAllowed(null, port, out var reason));
        Assert.Contains("非法", reason);
    }

    [Fact]
    public void 单值条目_命中放行_未命中拒绝()
    {
        var ports = new List<AllowPortOptions> { new() { Single = 8080 } };

        Assert.True(PortPolicy.IsPortAllowed(ports, 8080, out _));
        Assert.False(PortPolicy.IsPortAllowed(ports, 8081, out var reason));
        Assert.Contains("白名单", reason);
    }

    [Fact]
    public void 范围条目_含两端命中_区间外拒绝()
    {
        var ports = new List<AllowPortOptions> { new() { Start = 10000, End = 20000 } };

        Assert.True(PortPolicy.IsPortAllowed(ports, 10000, out _));
        Assert.True(PortPolicy.IsPortAllowed(ports, 20000, out _));
        Assert.True(PortPolicy.IsPortAllowed(ports, 15000, out _));
        Assert.False(PortPolicy.IsPortAllowed(ports, 9999, out _));
        Assert.False(PortPolicy.IsPortAllowed(ports, 20001, out _));
    }

    [Fact]
    public void 多条目取并集()
    {
        var ports = new List<AllowPortOptions>
        {
            new() { Start = 10000, End = 10002 },
            new() { Single = 8080 },
        };

        Assert.True(PortPolicy.IsPortAllowed(ports, 8080, out _));
        Assert.True(PortPolicy.IsPortAllowed(ports, 10001, out _));
        Assert.False(PortPolicy.IsPortAllowed(ports, 10003, out _));
    }

    [Fact]
    public void 非法条目被跳过_全部非法时拒绝并提示配置问题()
    {
        // Start > End、全 0、null 都不是有效条目
        var ports = new List<AllowPortOptions?>
        {
            null,
            new() { Start = 20000, End = 10000 },
            new(),
        };

        Assert.False(PortPolicy.IsPortAllowed(ports!, 15000, out var reason));
        Assert.Contains("白名单", reason);
    }

    [Fact]
    public void 合法条目混在非法条目中仍生效()
    {
        var ports = new List<AllowPortOptions?>
        {
            null,
            new() { Start = 20000, End = 10000 },
            new() { Single = 8080 },
        };

        Assert.True(PortPolicy.IsPortAllowed(ports!, 8080, out _));
        Assert.False(PortPolicy.IsPortAllowed(ports!, 8081, out _));
    }
}
