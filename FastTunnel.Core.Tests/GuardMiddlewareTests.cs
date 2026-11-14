// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Forwarder.Kestrel.MiddleWare;
using FastTunnel.Core.Models;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// S2 的关键不变量：熔断摘掉 WebList 映射之后，请求仍必须拿到 451 而不是退化成 404。
/// GuardMiddleware 挂在 Initializer 之后、Forwarder 之前，因此一条判定覆盖 mux 与回连两条路径。
/// </summary>
public class GuardMiddlewareTests
{
    private const string Request = "GET /index.html?q=1 HTTP/1.1\r\nHost: test.test.cc\r\n\r\n";

    private sealed class Harness
    {
        public Harness(string[]? allow = null, bool registerWeb = true)
        {
            Config = new DefaultServerConfig();
            if (allow != null)
            {
                Config.WebAllowAccessIps = allow;
            }

            Bans = new BanRegistry(NullLogger<BanRegistry>.Instance, (string?)null);
            Audit = new AuditLog(NullLogger<AuditLog>.Instance, (DefaultServerConfig.AuditOptions?)null);
            Server = new FastTunnelServer(
                NullLogger<FastTunnelServer>.Instance,
                new TestOptionsMonitor<DefaultServerConfig>(Config),
                Bans);

            if (registerWeb)
            {
                var web = new WebInfo
                {
                    Host = "test.test.cc",
                    WebConfig = new WebConfig { LocalIp = "127.0.0.1", LocalPort = 80 },
                };
                Server.WebList["test.test.cc"] = WebHostGroup.ForSingle(web);
            }

            var pair = TestPipes.CreatePair();
            Peer = pair.B;
            Context = new TestConnectionContext(pair.A)
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 55555),
            };
        }

        public DefaultServerConfig Config { get; }

        public BanRegistry Bans { get; }

        public AuditLog Audit { get; }

        public FastTunnelServer Server { get; }

        public IDuplexPipe Peer { get; }

        public TestConnectionContext Context { get; }

        public void BanHost(string host)
            => Bans.Add(new BanRecord { SubjectType = BanSubjectType.Host, Subject = host });

        public async Task<(bool NextCalled, string Response)> RunAsync(string request)
        {
            await TestPipes.WriteAsync(Peer, request);
            await new FastTunelProtocol(Context, Server).TryAnalysisPipeAsync();

            var nextCalled = false;
            var guard = new GuardMiddleware(
                async ctx =>
                {
                    nextCalled = true;
                    await ctx.Transport.Input.CompleteAsync();
                    await ctx.Transport.Output.CompleteAsync();
                },
                NullLogger<GuardMiddleware>.Instance, Server, Bans, Audit);

            await guard.OnConnectionAsync(Context);

            var token = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
            var response = await TestPipes.ReadToEndAsStringAsync(Peer.Input, token);
            return (nextCalled, response);
        }
    }

    [Fact]
    public async Task 摘掉映射后被封域名仍返回451且不进转发()
    {
        var h = new Harness();
        h.BanHost("test.test.cc");
        Assert.True(h.Server.WebList.TryRemove("test.test.cc", out _), "模拟熔断已摘除映射");

        var (nextCalled, response) = await h.RunAsync(Request);

        Assert.False(nextCalled);
        Assert.Contains("HTTP/1.1 451 Unavailable For Legal Reasons", response);
        Assert.Contains("Connection: close", response);
        Assert.Single(h.Audit.Query(action: "blocked_request"));
        Assert.Equal("test.test.cc", h.Audit.Query(action: "blocked_request").Single().Host);
    }

    [Fact]
    public async Task 映射仍在时被封域名同样返回451()
    {
        var h = new Harness();
        h.BanHost("test.test.cc");

        var (nextCalled, response) = await h.RunAsync(Request);

        Assert.False(nextCalled);
        Assert.Contains("451 Unavailable For Legal Reasons", response);
    }

    [Fact]
    public async Task 未封禁请求放行到下一层()
    {
        var h = new Harness();

        var (nextCalled, response) = await h.RunAsync(Request);

        Assert.True(nextCalled);
        Assert.DoesNotContain("451", response);
        Assert.DoesNotContain("403", response);
    }

    [Fact]
    public async Task 封禁优先于访问白名单()
    {
        var h = new Harness(allow: new[] { "10.0.0.0/8" });
        h.BanHost("test.test.cc");

        var (_, response) = await h.RunAsync(Request);

        Assert.Contains("451 Unavailable For Legal Reasons", response);
        Assert.DoesNotContain("403 Forbidden", response);
    }

    [Fact]
    public async Task 白名单拒绝仍由Guard返回403()
    {
        var h = new Harness(allow: new[] { "10.0.0.0/8" });

        var (nextCalled, response) = await h.RunAsync(Request);

        Assert.False(nextCalled);
        Assert.Contains("HTTP/1.1 403 Forbidden", response);
        Assert.Single(h.Audit.Query(action: "denied_by_allowlist"));
    }

    [Fact]
    public async Task 控制面与数据面承载连接不受访客封禁影响()
    {
        var h = new Harness();
        h.BanHost("test.test.cc");

        var (nextCalled, response) = await h.RunAsync(
            "PROXY /00000000-0000-0000-0000-000000000001 HTTP/1.1\r\n\r\n");

        Assert.True(nextCalled);
        Assert.DoesNotContain("451", response);
    }

    [Fact]
    public async Task Mux端点请求不按Host匹配也不误封()
    {
        var h = new Harness();
        h.BanHost("test.test.cc");

        var (nextCalled, response) = await h.RunAsync(
            "GET /mux/00000000-0000-0000-0000-000000000001 HTTP/1.1\r\nHost: test.test.cc\r\n\r\n");

        Assert.True(nextCalled);
        Assert.DoesNotContain("451", response);
    }

    [Fact]
    public async Task 封禁命中时不会进入Forwarder的转发分叉()
    {
        // 完整链路 Guard -> Forwarder：封禁命中时 Forwarder 的 next 不应被调用，
        // 也不会发出 SwapMsg。用 next 计数作为「是否进入转发分叉」的探针。
        var h = new Harness();
        h.BanHost("test.test.cc");
        h.Server.WebList.TryRemove("test.test.cc", out _);
        await TestPipes.WriteAsync(h.Peer, Request);
        await new FastTunelProtocol(h.Context, h.Server).TryAnalysisPipeAsync();

        var forwarderNextCalled = false;
        var forwarder = new ForwarderMiddleware(
            _ =>
            {
                forwarderNextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<ForwarderMiddleware>.Instance, h.Server);

        var guard = new GuardMiddleware(
            ctx => forwarder.OnConnectionAsync(ctx),
            NullLogger<GuardMiddleware>.Instance, h.Server, h.Bans, h.Audit);

        await guard.OnConnectionAsync(h.Context);
        var response = await TestPipes.ReadToEndAsStringAsync(
            h.Peer.Input, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.False(forwarderNextCalled);
        Assert.Contains("451 Unavailable For Legal Reasons", response);
    }
}
