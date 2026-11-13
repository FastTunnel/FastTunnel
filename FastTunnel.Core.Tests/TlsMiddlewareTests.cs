// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder.Kestrel.MiddleWare;
using FastTunnel.Core.Refs;
using FastTunnel.Core.Tests.Support;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 服务端 1270 端口的首字节分流：明文 HTTP 与隧道客户端的 TLS 走同一个端口。
///
/// 这里盯的是两个曾经把链路挂死的细节：
/// - 明文分支必须「一个字节都不消费、也不算已检查」，否则后面的 HTTP 解析器永远等新数据；
/// - TLS 分支交给 SslStream 之前同样不能消费首字节，否则 ClientHello 被吃掉，握手永远不完成。
/// </summary>
public class TlsMiddlewareTests
{
    private static CancellationToken TestToken(TimeSpan? timeout = null)
        => new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15)).Token;

    private static ServerCertificateProvider CreateProvider(TempDirectory dir, bool enabled = true)
        => new(
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig
            {
                WebDomain = "test.test.cc",
                Tls = new DefaultServerConfig.TlsOptions { Enabled = enabled, AutoGenerate = true },
            }),
            new TestHostEnvironment(dir.Path),
            NullLogger<ServerCertificateProvider>.Instance);

    [Fact]
    public async Task 明文请求一个字节都不消费_原样交给后续中间件()
    {
        using var dir = new TempDirectory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 明文分支是单端自环：模拟「客户端发来的字节」写进同一条管道
        var context = new TestConnectionContext(TestPipes.CreateSelfLoop());
        var provider = CreateProvider(dir);

        const string request = "GET /hello HTTP/1.1\r\nHost: test.test.cc\r\n\r\n";
        await TestPipes.WriteAsync(context.Transport, request, cts.Token);

        string? observed = null;
        var middleware = new TlsMiddleware(
            async ctx =>
            {
                var bytes = await TestPipes.ReadExactAsync(
                    ctx.Transport.Input, Encoding.UTF8.GetByteCount(request), cts.Token);
                observed = Encoding.UTF8.GetString(bytes);
            },
            NullLogger<TlsMiddleware>.Instance,
            provider);

        await middleware.OnConnectionAsync(context);

        Assert.Equal(request, observed);
    }

    [Fact]
    public async Task TLS连接完成握手并透传明文()
    {
        using var dir = new TempDirectory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (serverPipe, clientPipe) = TestPipes.CreatePair();
        var context = new TestConnectionContext(serverPipe);
        var provider = CreateProvider(dir);

        const string request = "MUX /00000000000000000000000000000000 HTTP/1.1\r\nHost: localhost\r\n\r\n";
        var expected = Encoding.UTF8.GetBytes(request);

        string? observed = null;
        var middleware = new TlsMiddleware(
            async ctx =>
            {
                var bytes = await TestPipes.ReadExactAsync(ctx.Transport.Input, expected.Length, cts.Token);
                observed = Encoding.UTF8.GetString(bytes);
            },
            NullLogger<TlsMiddleware>.Instance,
            provider);

        var middlewareTask = middleware.OnConnectionAsync(context);

        using var rawStream = new DuplexPipeStream(clientPipe.Input, clientPipe.Output);
        using var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false, (_, _, _, _) => true);

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, cts.Token);

        Assert.True(ssl.IsEncrypted);
        Assert.True(ssl.IsAuthenticated);

        await ssl.WriteAsync(expected, cts.Token);
        await ssl.FlushAsync(cts.Token);

        await middlewareTask.WaitAsync(TimeSpan.FromSeconds(10));

        // 解密后的字节与客户端写入的完全一致
        Assert.Equal(request, observed);
    }

    [Fact]
    public async Task 服务端关闭TLS时收到ClientHello直接断开()
    {
        using var dir = new TempDirectory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (serverPipe, clientPipe) = TestPipes.CreatePair();
        var context = new TestConnectionContext(serverPipe);
        var provider = CreateProvider(dir, enabled: false);

        var nextCalled = false;
        var middleware = new TlsMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<TlsMiddleware>.Instance,
            provider);

        var middlewareTask = middleware.OnConnectionAsync(context);

        using var rawStream = new DuplexPipeStream(clientPipe.Input, clientPipe.Output);
        using var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false, (_, _, _, _) => true);

        // 服务端不握手而是直接断开，客户端必须尽快失败而不是挂到超时
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }, cts.Token));

        await middlewareTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task 空连接不阻塞后续中间件()
    {
        using var dir = new TempDirectory();

        var (serverPipe, clientPipe) = TestPipes.CreatePair();
        var context = new TestConnectionContext(serverPipe);
        var provider = CreateProvider(dir);

        var nextCalled = false;
        var middleware = new TlsMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<TlsMiddleware>.Instance,
            provider);

        // 对端什么都没发就断开
        clientPipe.Output.Complete();

        await middleware.OnConnectionAsync(context).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(nextCalled);
    }
}
