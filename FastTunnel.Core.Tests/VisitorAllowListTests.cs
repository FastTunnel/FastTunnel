// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
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
/// 访问白名单的接线点：S2 起由 GuardMiddleware 统一判定（451 封禁 + 403 白名单），
/// 它挂在 Initializer 之后、ForwarderMiddleware 之前，因此拦在 waitSwap 分叉之前，
/// mux 与回连两条路径都覆盖。用一对互连管道，把「服务端写回的响应」与「访客发来的请求」分开读。
/// </summary>
public class VisitorAllowListTests
{
    private sealed class Harness
    {
        public Harness(string[]? allow = null, string[]? trusted = null)
        {
            var config = new DefaultServerConfig();
            if (allow != null)
            {
                config.WebAllowAccessIps = allow;
            }

            if (trusted != null)
            {
                config.TrustedProxyIps = trusted;
            }

            Bans = new BanRegistry(NullLogger<BanRegistry>.Instance, (string?)null);
            Server = new FastTunnelServer(
                NullLogger<FastTunnelServer>.Instance,
                new TestOptionsMonitor<DefaultServerConfig>(config),
                Bans);

            Server.WebList["test.test.cc"] = WebHostGroup.ForSingle(new WebInfo
            {
                Host = "test.test.cc",
                WebConfig = new WebConfig { LocalIp = "127.0.0.1", LocalPort = 80 },
            });

            var pair = TestPipes.CreatePair();
            Peer = pair.B;
            Context = new TestConnectionContext(pair.A)
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 55555),
            };
        }

        public BanRegistry Bans { get; }

        public FastTunnelServer Server { get; }

        public IDuplexPipe Peer { get; }

        public TestConnectionContext Context { get; }

        public async Task<(bool NextCalled, string Response)> RunAsync(string request, bool abortBefore = false)
        {
            await TestPipes.WriteAsync(Peer, request);
            await new FastTunelProtocol(Context, Server).TryAnalysisPipeAsync();

            if (abortBefore)
            {
                Context.Abort();
            }

            var nextCalled = false;
            var guard = new GuardMiddleware(
                async ctx =>
                {
                    nextCalled = true;
                    await ctx.Transport.Input.CompleteAsync();
                    await ctx.Transport.Output.CompleteAsync();
                },
                NullLogger<GuardMiddleware>.Instance,
                Server,
                Bans);

            await guard.OnConnectionAsync(Context);

            var token = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
            var response = await TestPipes.ReadToEndAsStringAsync(Peer.Input, token);
            return (nextCalled, response);
        }
    }

    private const string Request = "GET / HTTP/1.1\r\nHost: test.test.cc\r\n\r\n";

    [Fact]
    public async Task 不在白名单的访客收到403且不进入转发()
    {
        var harness = new Harness(allow: new[] { "10.0.0.0/8" });

        var (nextCalled, response) = await harness.RunAsync(Request);

        Assert.False(nextCalled);
        Assert.Contains("HTTP/1.1 403 Forbidden", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task 访客自带XFF声称白名单内地址也拿不到转发()
    {
        var harness = new Harness(allow: new[] { "10.0.0.0/8" });

        var (_, response) = await harness.RunAsync(
            "GET / HTTP/1.1\r\nHost: test.test.cc\r\nX-Forwarded-For: 10.0.0.1\r\n\r\n");

        Assert.Contains("HTTP/1.1 403 Forbidden", response);
    }

    [Fact]
    public async Task 可信代理透传的XFF命中白名单时放行()
    {
        var harness = new Harness(
            allow: new[] { "10.0.0.0/8" },
            trusted: new[] { "203.0.113.9" });

        var (nextCalled, response) = await harness.RunAsync(
            "GET / HTTP/1.1\r\nHost: test.test.cc\r\nX-Forwarded-For: 10.0.0.7\r\n\r\n",
            abortBefore: true);

        Assert.True(nextCalled);
        Assert.DoesNotContain("403 Forbidden", response);
    }

    [Fact]
    public async Task 白名单为空时不拦截正常访客()
    {
        var harness = new Harness();

        var (nextCalled, response) = await harness.RunAsync(Request, abortBefore: true);

        Assert.True(nextCalled);
        Assert.DoesNotContain("403 Forbidden", response);
    }
}
