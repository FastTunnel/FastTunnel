// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
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
/// 连接建立后的首段字节解析：判断这是内网站点的 HTTP 访问、还是隧道客户端的 PROXY / MUX 请求。
///
/// 两个关键不变量：
/// 1. 普通 HTTP 请求一个字节都不能消费掉（否则后面的 Kestrel 收不到请求头）；
/// 2. PROXY / MUX 的握手头必须消费干净（否则残留的 CRLF 会污染紧随其后的二进制帧）。
/// 这两条以前都错过一次，所以单独锁住。
/// </summary>
public class FastTunelProtocolTests
{
    private sealed class TestCase
    {
        public TestCase(DefaultServerConfig? config = null)
        {
            Config = config ?? new DefaultServerConfig();

            var logger = NullLogger<FastTunnelServer>.Instance;
            Server = new FastTunnelServer(logger, new TestOptionsMonitor<DefaultServerConfig>(Config));

            // 这里只测连接层一侧：写入的字节要能从同一个上下文里读回来
            Context = new TestConnectionContext(TestPipes.CreateSelfLoop());
            Protocol = new FastTunelProtocol(Context, Server);
        }

        public DefaultServerConfig Config { get; }

        public FastTunnelServer Server { get; }

        public TestConnectionContext Context { get; }

        public FastTunelProtocol Protocol { get; }

        public WebInfo AddWeb(string host)
        {
            var web = new WebInfo { WebConfig = new WebConfig { SubDomain = host } };
            Server.WebList[host] = web;
            return web;
        }

        public Task FeedAsync(string text) => TestPipes.WriteAsync(Context.Transport, text);
    }

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Fact]
    public async Task 普通HTTP请求按Host命中内网站点()
    {
        var test = new TestCase();
        var web = test.AddWeb("test.test.cc");

        await test.FeedAsync("GET /index.html HTTP/1.1\r\nHost: test.test.cc:1270\r\nUser-Agent: x\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal("GET", test.Protocol.Method);
        Assert.Equal("/index.html", test.Protocol.Path);
        Assert.Equal("test.test.cc", test.Protocol.Host);
        Assert.Same(web, test.Protocol.MatchWeb);
    }

    [Fact]
    public async Task 普通HTTP请求不消费任何字节()
    {
        var test = new TestCase();
        test.AddWeb("test.test.cc");

        const string request = "GET / HTTP/1.1\r\nHost: test.test.cc\r\n\r\n";
        await test.FeedAsync(request);

        await test.Protocol.TryAnalysisPipeAsync();

        // 请求必须原样留给后面的中间件；少一个字节 Kestrel 就会解析失败
        var remaining = await TestPipes.ReadExactAsync(
            test.Context.Transport.Input, Encoding.UTF8.GetByteCount(request), TestToken());

        Assert.Equal(request, Encoding.UTF8.GetString(remaining));
    }

    [Fact]
    public async Task IPv6字面量Host不被切坏()
    {
        var test = new TestCase();

        await test.FeedAsync("GET / HTTP/1.1\r\nHost: [::1]:1270\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        // 以前 Host.Split(':')[0] 会把它截成 "["
        Assert.Equal("::1", test.Protocol.Host);
        Assert.Null(test.Protocol.MatchWeb);
    }

    [Fact]
    public async Task Mux端点不做Host匹配()
    {
        var test = new TestCase();
        var web = test.AddWeb("test.test.cc");

        var muxId = Guid.NewGuid();
        await test.FeedAsync($"GET {ProtocolConst.MUX_PATH}/{muxId:N} HTTP/1.1\r\nHost: test.test.cc:1270\r\nUpgrade: websocket\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        // 客户端连的域名同时也是内网站点域名时，隧道端点不能被当成普通 HTTP 转走
        Assert.Null(test.Protocol.MatchWeb);
        Assert.Equal("GET", test.Protocol.Method);
        Assert.NotSame(web, test.Protocol.MatchWeb);
    }

    [Theory]
    [InlineData("/muxx")]
    [InlineData("/muxx/path")]
    [InlineData("/other/mux")]
    public async Task 非mux前缀仍然按Host匹配(string path)
    {
        var test = new TestCase();
        var web = test.AddWeb("test.test.cc");

        await test.FeedAsync($"GET {path} HTTP/1.1\r\nHost: test.test.cc\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Same(web, test.Protocol.MatchWeb);
    }

    [Fact]
    public async Task Proxy请求解析出消息Id并消费握手头()
    {
        var test = new TestCase();
        var id = Guid.NewGuid();

        await test.FeedAsync($"PROXY /{id} HTTP/1.1\r\nHost: 127.0.0.1:1270\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal("PROXY", test.Protocol.Method);
        Assert.Equal(id, test.Protocol.MessageId);
        Assert.Null(test.Protocol.MatchWeb);

        // 握手头之后的字节才是隧道数据，这里没有后续数据，所以应该读不到任何东西
        var feature = test.Context.Features.Get<IFastTunnelFeature>();
        Assert.Equal("PROXY", feature.Method);
        Assert.Equal(id, feature.MessageId);
    }

    [Fact]
    public async Task Mux请求解析出会话Id()
    {
        var test = new TestCase();
        var id = Guid.NewGuid();

        await test.FeedAsync($"MUX /{id} HTTP/1.1\r\nHost: 127.0.0.1:1270\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Equal("MUX", test.Protocol.Method);
        Assert.Equal(id, test.Protocol.MessageId);
    }

    [Fact]
    public async Task Mux握手头之后的二进制帧不受污染()
    {
        var test = new TestCase();
        var id = Guid.NewGuid();

        // MUX 之后紧跟的就是帧字节，中间不能残留任何 CRLF
        var payload = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xAA };

        await test.FeedAsync($"MUX /{id} HTTP/1.1\r\nHost: 127.0.0.1:1270\r\n\r\n");
        await TestPipes.WriteAsync(test.Context.Transport, payload);

        await test.Protocol.TryAnalysisPipeAsync();

        var remaining = await TestPipes.ReadExactAsync(test.Context.Transport.Input, payload.Length, TestToken());
        Assert.Equal(payload, remaining);
    }

    [Fact]
    public async Task Host匹配是大小写不敏感的()
    {
        var test = new TestCase();
        var web = test.AddWeb("test.test.cc");

        await test.FeedAsync("GET / HTTP/1.1\r\nHOST: TEST.TEST.CC:1270\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Same(web, test.Protocol.MatchWeb);
        Assert.Equal("test.test.cc", test.Protocol.Host);
    }

    [Fact]
    public async Task 未匹配的Host不算隧道流量()
    {
        var test = new TestCase();
        test.AddWeb("test.test.cc");

        await test.FeedAsync("GET / HTTP/1.1\r\nHost: unknown.test.cc\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        Assert.Null(test.Protocol.MatchWeb);
    }

    [Fact]
    public async Task 请求行没有路径时不崩()
    {
        var test = new TestCase();

        // 畸形请求行：没有第二个空格
        await test.FeedAsync("BROKEN\r\nHost: test.test.cc\r\n\r\n");

        await test.Protocol.TryAnalysisPipeAsync();

        // 请求行缺少空白分隔时按整行当方法名处理（既有行为），关键是不能抛异常
        Assert.StartsWith("BROKEN", test.Protocol.Method);
        Assert.Null(test.Protocol.Path);
    }
}
