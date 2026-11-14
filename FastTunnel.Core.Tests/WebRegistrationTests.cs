// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net;
using System.Net.WebSockets;
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 域名路由表的占用与清理语义。以前是 AddOrUpdate 无条件覆盖 + LogOut() 空实现，
/// 既能劫持他人域名，又会在下线后留下永远清不掉的残留映射。
/// </summary>
public class WebRegistrationTests
{
    private static FastTunnelServer CreateServer()
        => new(NullLogger<FastTunnelServer>.Instance,
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig { WebDomain = "test.cc" }));

    private static TunnelClient CreateClient(FastTunnelServer server)
        => new(new ClientWebSocket(), server,
            new LoginHandler(NullLogger<LoginHandler>.Instance),
            IPAddress.Loopback, NullLogger<TunnelClient>.Instance);

    private static WebInfo CreateWeb(TunnelClient client, string host)
        => new()
        {
            Host = host,
            Socket = client.webSocket,
            WebConfig = new WebConfig { SubDomain = host.Split('.')[0], WWW = Array.Empty<string>() },
            Client = client,
        };

    [Fact]
    public void 首次登记写入路由表()
    {
        var server = CreateServer();
        var client = CreateClient(server);
        var web = CreateWeb(client, "www.example.com");

        Assert.True(server.TryRegisterWeb(web.Host, web, out _));
        Assert.Same(web, server.WebList[web.Host].PickMember());
    }

    [Fact]
    public void 其它在线客户端不能覆盖已有映射()
    {
        var server = CreateServer();
        var owner = CreateClient(server);
        var attacker = CreateClient(server);
        var original = CreateWeb(owner, "www.example.com");

        Assert.True(server.TryRegisterWeb(original.Host, original, out _));
        Assert.False(server.TryRegisterWeb(original.Host, CreateWeb(attacker, original.Host), out _));
        Assert.Same(original, server.WebList[original.Host].PickMember());
    }

    [Fact]
    public void 同一客户端重复LogIn允许刷新映射()
    {
        var server = CreateServer();
        var client = CreateClient(server);
        var first = CreateWeb(client, "www.example.com");
        var second = CreateWeb(client, "www.example.com");

        Assert.True(server.TryRegisterWeb(first.Host, first, out _));
        Assert.True(server.TryRegisterWeb(second.Host, second, out _));
        Assert.Same(second, server.WebList[first.Host].PickMember());
    }

    [Fact]
    public void 旧会话已离线时允许接管域名()
    {
        var server = CreateServer();
        var oldClient = CreateClient(server);
        var newClient = CreateClient(server);
        var stale = CreateWeb(oldClient, "www.example.com");

        Assert.True(server.TryRegisterWeb(stale.Host, stale, out _));

        // 模拟「旧会话已标记离线但清理与新登录并发」的窗口：旧映射短暂残留
        oldClient.Logout();
        server.WebList[stale.Host] = WebHostGroup.ForSingle(stale);

        var fresh = CreateWeb(newClient, stale.Host);
        Assert.True(server.TryRegisterWeb(fresh.Host, fresh, out _));

        // 并发窗口收敛：Logout 的清理随后摘除旧实例，路由只命中新成员
        server.RemoveWeb(stale);
        Assert.True(server.TryGetWebProxyByHost(stale.Host, out var picked));
        Assert.Same(fresh, picked);
    }

    [Fact]
    public void 无主映射不允许被覆盖()
    {
        var server = CreateServer();
        var client = CreateClient(server);
        server.WebList["orphan.example.com"] = WebHostGroup.ForSingle(new WebInfo { Host = "orphan.example.com" });

        Assert.False(server.TryRegisterWeb("orphan.example.com", CreateWeb(client, "orphan.example.com"), out var reason));
        Assert.Contains("无主", reason);
    }

    [Fact]
    public void RemoveWeb按实例精确摘除不误删新归属()
    {
        var server = CreateServer();
        var oldClient = CreateClient(server);
        var newClient = CreateClient(server);
        var stale = CreateWeb(oldClient, "www.example.com");
        var fresh = CreateWeb(newClient, "www.example.com");

        server.WebList[stale.Host] = WebHostGroup.ForSingle(fresh);

        server.RemoveWeb(stale);

        Assert.Same(fresh, server.WebList[stale.Host].PickMember());
    }

    [Fact]
    public void 会话下线摘除全部自有域名映射()
    {
        var server = CreateServer();
        var client = CreateClient(server);
        var subDomain = CreateWeb(client, "test.test.cc");
        var custom1 = CreateWeb(client, "www.example.com");
        var custom2 = CreateWeb(client, "shop.example.com");
        var otherClient = CreateClient(server);
        var others = CreateWeb(otherClient, "other.example.com");

        foreach (var web in new[] { subDomain, custom1, custom2, others })
        {
            Assert.True(server.TryRegisterWeb(web.Host, web, out _));
        }

        client.AddWeb(subDomain);
        client.AddWeb(custom1);
        client.AddWeb(custom2);
        otherClient.AddWeb(others);

        client.Logout();

        Assert.True(client.IsOffline);
        Assert.False(server.WebList.ContainsKey(subDomain.Host));
        Assert.False(server.WebList.ContainsKey(custom1.Host));
        Assert.False(server.WebList.ContainsKey(custom2.Host));
        Assert.Same(others, server.WebList[others.Host].PickMember());
    }
}
