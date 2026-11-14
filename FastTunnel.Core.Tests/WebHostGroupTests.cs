// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 代理组负载均衡：路由表 host → 组容器的聚合、冲突拒绝、选路与被动摘除。
/// 对齐 frp loadBalancer.group 语义：同组同口令同域名自动成组，口令不一致拒绝入组。
/// </summary>
public class WebHostGroupTests
{
    private static FastTunnelServer Server(LoadBalancerOptions? lb = null)
        => new(NullLogger<FastTunnelServer>.Instance,
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig
            {
                WebDomain = "test.cc",
                LoadBalancer = lb ?? new LoadBalancerOptions(),
            }));

    private static TunnelClient Client(FastTunnelServer server)
        => new(new StubWebSocket(), server,
            new TestLoginHandler(), IPAddress.Loopback, NullLogger<TunnelClient>.Instance);

    private sealed class TestLoginHandler : LoginHandler
    {
        public TestLoginHandler() : base(NullLogger<LoginHandler>.Instance)
        {
        }
    }

    /// <summary>不会真正收发的最小 WebSocket：让登录流程里的日志回写能通过。</summary>
    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Text, true));

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken cancellationToken)
            => new(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Text, true));

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static WebInfo Web(TunnelClient client, string host, string? group = null, string? groupKey = null)
        => new()
        {
            Host = host,
            Socket = client.webSocket,
            WebConfig = new WebConfig
            {
                SubDomain = host,
                LocalIp = "127.0.0.1",
                LocalPort = 80,
                Group = group,
                GroupKey = groupKey,
            },
            Client = client,
        };

    [Fact]
    public void 无组注册_单成员组_行为与旧版一致()
    {
        var server = Server();
        var alice = Client(server);
        var web = Web(alice, "app.test.cc");

        Assert.True(server.TryRegisterWeb(web.Host, web, out var reason));
        Assert.Empty(reason);
        Assert.Equal(1, server.WebList["app.test.cc"].MemberCount);
        Assert.Same(web, server.TryGetWebProxyByHost("app.test.cc", out var picked) ? picked : null);
        Assert.Same(web, picked);
    }

    [Fact]
    public void 无组同域名_他人在线注册被拒()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);
        var webA = Web(alice, "app.test.cc");

        Assert.True(server.TryRegisterWeb(webA.Host, webA, out _));
        var webB = Web(bob, "app.test.cc");
        Assert.False(server.TryRegisterWeb(webB.Host, webB, out var reason));
        Assert.Contains("占用", reason);
        Assert.Equal(1, server.WebList["app.test.cc"].MemberCount);
    }

    [Fact]
    public void 同组同口令_自动聚合为多成员组()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "s3cret");
        var webB = Web(bob, "app.test.cc", group: "web", groupKey: "s3cret");

        Assert.True(server.TryRegisterWeb(webA.Host, webA, out _));
        Assert.True(server.TryRegisterWeb(webB.Host, webB, out _));

        var group = server.WebList["app.test.cc"];
        Assert.Equal(2, group.MemberCount);
        Assert.Equal("web", group.Group);

        // 选路只会命中组内成员
        for (var i = 0; i < 50; i++)
        {
            Assert.True(server.TryGetWebProxyByHost("app.test.cc", out var picked));
            Assert.True(ReferenceEquals(picked, webA) || ReferenceEquals(picked, webB));
        }
    }

    [Fact]
    public void 组口令不一致_拒绝入组()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "right");
        Assert.True(server.TryRegisterWeb(webA.Host, webA, out _));

        var webB = Web(bob, "app.test.cc", group: "web", groupKey: "wrong");
        Assert.False(server.TryRegisterWeb(webB.Host, webB, out var reason));
        Assert.Contains("口令", reason);
        Assert.Equal(1, server.WebList["app.test.cc"].MemberCount);
    }

    [Fact]
    public void 组名不一致_拒绝入组()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "s3cret");
        Assert.True(server.TryRegisterWeb(webA.Host, webA, out _));

        var webB = Web(bob, "app.test.cc", group: "other", groupKey: "s3cret");
        Assert.False(server.TryRegisterWeb(webB.Host, webB, out var reason));
        Assert.Contains("负载均衡组", reason);
    }

    [Fact]
    public void 已有组的域名_无组注册被拒()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "s3cret");
        Assert.True(server.TryRegisterWeb(webA.Host, webA, out _));

        var webB = Web(bob, "app.test.cc");
        Assert.False(server.TryRegisterWeb(webB.Host, webB, out _));
    }

    [Fact]
    public void 同客户端重复注册_原地刷新不增加成员()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA1 = Web(alice, "app.test.cc", group: "web", groupKey: "k");
        var webB = Web(bob, "app.test.cc", group: "web", groupKey: "k");
        Assert.True(server.TryRegisterWeb(webA1.Host, webA1, out _));
        Assert.True(server.TryRegisterWeb(webB.Host, webB, out _));

        // alice 重连刷新：端口变了，成员数不变，旧实例被替换
        var webA2 = Web(alice, "app.test.cc", group: "web", groupKey: "k");
        webA2.WebConfig.LocalPort = 8080;
        Assert.True(server.TryRegisterWeb(webA2.Host, webA2, out _));

        var group = server.WebList["app.test.cc"];
        Assert.Equal(2, group.MemberCount);
        Assert.All(group.SnapshotMembers(), m => Assert.NotSame(webA1, m));
    }

    [Fact]
    public void 成员退出_组空时路由条目随之摘除()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "k");
        var webB = Web(bob, "app.test.cc", group: "web", groupKey: "k");
        server.TryRegisterWeb(webA.Host, webA, out _);
        server.TryRegisterWeb(webB.Host, webB, out _);

        server.RemoveWeb(webA);
        Assert.True(server.WebList.ContainsKey("app.test.cc"));
        Assert.True(server.TryGetWebProxyByHost("app.test.cc", out var picked));
        Assert.Same(webB, picked);

        server.RemoveWeb(webB);
        Assert.False(server.WebList.ContainsKey("app.test.cc"));
        Assert.False(server.TryGetWebProxyByHost("app.test.cc", out _));
    }

    [Fact]
    public void 被动摘除_连续失败出池_成功清零恢复()
    {
        var lb = new LoadBalancerOptions { FailureThreshold = 3, RecoverySeconds = 60 };
        var server = Server(lb);
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "k");
        var webB = Web(bob, "app.test.cc", group: "web", groupKey: "k");
        server.TryRegisterWeb(webA.Host, webA, out _);
        server.TryRegisterWeb(webB.Host, webB, out _);

        // 连续失败 2 次：仍在池内（阈值 3）
        webA.GroupRef!.ReportFailure(webA, lb.FailureThreshold, lb.RecoverySeconds);
        webA.GroupRef.ReportFailure(webA, lb.FailureThreshold, lb.RecoverySeconds);
        Assert.Contains(webA, Enumerable.Range(0, 50).Select(_ =>
            server.TryGetWebProxyByHost("app.test.cc", out var p) ? p : null));

        // 第 3 次失败 → 出池
        webA.GroupRef.ReportFailure(webA, lb.FailureThreshold, lb.RecoverySeconds);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(server.TryGetWebProxyByHost("app.test.cc", out var p));
            Assert.Same(webB, p);
        }

        // 成功信号清零 → 回池
        webA.GroupRef.ReportSuccess(webA);
        Assert.Contains(webA, Enumerable.Range(0, 50).Select(_ =>
            server.TryGetWebProxyByHost("app.test.cc", out var p) ? p : null));
    }

    [Fact]
    public void 全组不健康_仍然随机挑一个成员兜底()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA = Web(alice, "app.test.cc", group: "web", groupKey: "k");
        var webB = Web(bob, "app.test.cc", group: "web", groupKey: "k");
        server.TryRegisterWeb(webA.Host, webA, out _);
        server.TryRegisterWeb(webB.Host, webB, out _);

        webA.GroupRef!.ReportFailure(webA, 1, 3600);
        webB.GroupRef!.ReportFailure(webB, 1, 3600);

        // 池空兜底：仍返回成员，请求不至于 503
        Assert.True(server.TryGetWebProxyByHost("app.test.cc", out var picked));
        Assert.True(ReferenceEquals(picked, webA) || ReferenceEquals(picked, webB));
    }

    [Fact]
    public void SnapshotWebs_跨组拍平返回全部成员()
    {
        var server = Server();
        var alice = Client(server);
        var bob = Client(server);

        var webA1 = Web(alice, "app.test.cc", group: "g1", groupKey: "k");
        var webA2 = Web(alice, "api.test.cc");
        var webB1 = Web(bob, "app.test.cc", group: "g1", groupKey: "k");
        server.TryRegisterWeb(webA1.Host, webA1, out _);
        server.TryRegisterWeb(webA2.Host, webA2, out _);
        server.TryRegisterWeb(webB1.Host, webB1, out _);

        var snapshot = server.SnapshotWebs();
        Assert.Equal(3, snapshot.Length);
        Assert.Contains(webA1, snapshot);
        Assert.Contains(webA2, snapshot);
        Assert.Contains(webB1, snapshot);
    }
}
