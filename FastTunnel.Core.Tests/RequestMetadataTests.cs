// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Forwarder.Kestrel.Features;
using FastTunnel.Core.Models;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// S1 补进来的 L7 请求元数据。三条不变量：
/// 1. 解析只「看」不「吃」，普通 HTTP 请求的字节必须原样留给后面的 Kestrel；
/// 2. 只保留审计白名单内的头，Cookie/Authorization 等凭据绝不入库；
/// 3. 真实 IP 只在可信代理下采信转发头。
/// </summary>
public class RequestMetadataTests
{
    private sealed class TestCase
    {
        public TestCase()
        {
            Config = new DefaultServerConfig();
            Server = new FastTunnelServer(
                NullLogger<FastTunnelServer>.Instance,
                new TestOptionsMonitor<DefaultServerConfig>(Config));
            Context = new TestConnectionContext(TestPipes.CreateSelfLoop());
            Protocol = new FastTunelProtocol(Context, Server);
        }

        public DefaultServerConfig Config { get; }

        public FastTunnelServer Server { get; }

        public TestConnectionContext Context { get; }

        public FastTunelProtocol Protocol { get; }

        public WebInfo AddWeb(string host)
        {
            var web = new WebInfo { Host = host, WebConfig = new WebConfig { SubDomain = host } };
            Server.WebList[host] = web;
            return web;
        }

        public Task FeedAsync(string text) => TestPipes.WriteAsync(Context.Transport, text);
    }

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static IFastTunnelFeature Feature(TestCase test)
        => test.Context.Features.Get<IFastTunnelFeature>()!;

    [Fact]
    public async Task 审计头与Path进入Feature且凭据头被丢弃()
    {
        var test = new TestCase();
        var web = test.AddWeb("test.test.cc");

        await test.FeedAsync(
            "POST /upload?q=1 HTTP/1.1\r\n"
            + "Host: test.test.cc:1270\r\n"
            + "User-Agent: curl/8.0\r\n"
            + "Content-Type: text/plain\r\n"
            + "Content-Length: 5\r\n"
            + "Referer: https://example.com/\r\n"
            + "X-Forwarded-For: 203.0.113.7\r\n"
            + "Cookie: session=secret\r\n"
            + "Authorization: Bearer secret\r\n"
            + "\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        var feat = Feature(test);
        Assert.Same(web, feat.MatchWeb);
        Assert.Equal("POST", feat.Method);
        Assert.Equal("test.test.cc", feat.Host);
        Assert.Equal("/upload?q=1", feat.Path);
        Assert.Equal("curl/8.0", feat.Headers["user-agent"]);
        Assert.Equal("text/plain", feat.Headers["content-type"]);
        Assert.Equal("5", feat.Headers["content-length"]);
        Assert.Equal("https://example.com/", feat.Headers["referer"]);
        Assert.Equal("203.0.113.7", feat.Headers["x-forwarded-for"]);
        Assert.Equal("test.test.cc:1270", feat.Headers["host"]);
        Assert.False(feat.Headers.ContainsKey("cookie"));
        Assert.False(feat.Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task Host冒号后没有空格也能匹配()
    {
        var test = new TestCase();
        var web = test.AddWeb("test.test.cc");

        // 旧实现用 Split(" ")[1]，这种写法会 IndexOutOfRange
        await test.FeedAsync("GET / HTTP/1.1\r\nHost:test.test.cc\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal("test.test.cc", test.Protocol.Host);
        Assert.Same(web, test.Protocol.MatchWeb);
        Assert.Equal("test.test.cc", Feature(test).Headers["host"]);
    }

    [Fact]
    public async Task 带多Header的请求仍然不消费任何字节()
    {
        var test = new TestCase();
        test.AddWeb("test.test.cc");

        const string request = "GET / HTTP/1.1\r\nHost: test.test.cc\r\n"
            + "User-Agent: x\r\nX-Forwarded-For: 1.2.3.4\r\nCookie: a=b\r\n\r\n";
        await test.FeedAsync(request);

        await test.Protocol.TryAnalysisPipeAsync();

        var remaining = await TestPipes.ReadExactAsync(
            test.Context.Transport.Input, Encoding.UTF8.GetByteCount(request), TestToken());

        Assert.Equal(request, Encoding.UTF8.GetString(remaining));
    }

    [Fact]
    public async Task 可信代理下真实IP来自XFF()
    {
        var test = new TestCase();
        test.AddWeb("test.test.cc");
        test.Config.TrustedProxyIps = new[] { "127.0.0.1" };

        await test.FeedAsync("GET / HTTP/1.1\r\nHost: test.test.cc\r\nX-Forwarded-For: 203.0.113.7\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal(IPAddress.Parse("203.0.113.7"), test.Protocol.ClientIp);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Feature(test).ClientIp);
    }

    [Fact]
    public async Task 不可信来源的XFF被忽略()
    {
        var test = new TestCase();
        test.AddWeb("test.test.cc");

        await test.FeedAsync("GET / HTTP/1.1\r\nHost: test.test.cc\r\nX-Forwarded-For: 10.0.0.1\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal(IPAddress.Loopback, test.Protocol.ClientIp);
    }

    [Fact]
    public async Task Mux端点不产生访客请求头()
    {
        var test = new TestCase();
        var id = Guid.NewGuid();

        await test.FeedAsync($"MUX /{id} HTTP/1.1\r\nHost: test.test.cc\r\nCookie: secret\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        var feat = Feature(test);
        Assert.Equal("MUX", feat.Method);
        Assert.Equal(id, feat.MessageId);
        Assert.Empty(feat.Headers);
    }

    [Fact]
    public async Task IPv6字面量Host仍能匹配并原样保留()
    {
        var test = new TestCase();
        var web = test.AddWeb("::1");

        await test.FeedAsync("GET / HTTP/1.1\r\nHost: [::1]:1270\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal("::1", test.Protocol.Host);
        Assert.Same(web, test.Protocol.MatchWeb);
        Assert.Equal("[::1]:1270", Feature(test).Headers["host"]);
    }

    [Fact]
    public async Task 超长请求头被丢弃()
    {
        var test = new TestCase();
        test.AddWeb("test.test.cc");

        await test.FeedAsync("GET / HTTP/1.1\r\nHost: test.test.cc\r\nUser-Agent: "
            + new string('a', 9000) + "\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.False(Feature(test).Headers.ContainsKey("user-agent"));
        Assert.Equal("test.test.cc", Feature(test).Headers["host"]);
    }
}
