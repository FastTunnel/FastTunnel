// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// WebSocket 承载：把 MUX 帧流封装进 WebSocket 二进制消息。
///
/// 用真实的 Kestrel + ClientWebSocket 跑，而不是直接 mock <see cref="WebSocket"/>——
/// 要验的正是「消息边界」这件事：<c>WebSocketPipeWriter</c> 一次 Flush 必须恰好对应一条消息，
/// 接收侧再把消息摊平回连续字节流，两端 MUX 才能对齐帧。
/// </summary>
public class WebSocketDuplexPipeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>最小 WebSocket 服务端：接受 /mux 后把连接交给回调。</summary>
    private sealed class WsServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private WsServer(WebApplication app, Uri uri)
        {
            _app = app;
            Uri = uri;
        }

        public Uri Uri { get; }

        public static async Task<WsServer> StartAsync(Func<WebSocket, Task> onAccept)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            app.UseWebSockets();

            app.Run(async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                await onAccept(socket);
            });

            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;

            // Kestrel 监听 0 端口，实际端口会回填到地址里；ClientWebSocket 只认 ws/wss 方案
            var address = System.Linq.Enumerable.First(addresses).Replace("http://", "ws://");
            return new WsServer(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(cts.Token);
            }
            catch
            {
                // 测试结束时的清理失败不影响结论
            }

            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WebSocket承载下Mux双向传输正常()
    {
        var serverSessionReady = new TaskCompletionSource<MuxSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await WsServer.StartAsync(async socket =>
        {
            await using var pipe = new WebSocketDuplexPipe(socket);
            var session = new MuxSession(pipe, isServer: true, NullLogger.Instance);

            serverSessionReady.TrySetResult(session);
            await session.RunAsync();
        });

        using var cts = new CancellationTokenSource(Timeout);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(server.Uri, cts.Token);

        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var clientPipe = new WebSocketDuplexPipe(client);
        var clientSession = new MuxSession(clientPipe, isServer: false, NullLogger.Instance, (stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var clientRun = clientSession.RunAsync();

        var serverSession = await serverSessionReady.Task.WaitAsync(Timeout);

        try
        {
            await using var serverStream = await serverSession.OpenStreamAsync(
                new SwapRequest { Id = "ws-1", Host = "10.0.0.8", Port = 8080 }, cts.Token);

            var remote = await accepted.Task.WaitAsync(Timeout);

            // 下行
            var down = "hello over websocket"u8.ToArray();
            await serverStream.WriteAsync(down, cts.Token);
            Assert.Equal(down, await TestPipes.ReadExactAsync(remote.Input, down.Length, cts.Token));

            // 上行
            var up = "response over websocket"u8.ToArray();
            await remote.WriteAsync(up, cts.Token);
            Assert.Equal(up, await TestPipes.ReadExactAsync(serverStream.Input, up.Length, cts.Token));
        }
        finally
        {
            await serverSession.DisposeAsync();
            await clientSession.DisposeAsync();
            await Task.WhenAny(clientRun, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task WebSocket承载下大流量仍能无损坏传输()
    {
        var serverSessionReady = new TaskCompletionSource<MuxSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await WsServer.StartAsync(async socket =>
        {
            await using var pipe = new WebSocketDuplexPipe(socket);
            var session = new MuxSession(pipe, isServer: true, NullLogger.Instance);

            serverSessionReady.TrySetResult(session);
            await session.RunAsync();
        });

        using var cts = new CancellationTokenSource(Timeout);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(server.Uri, cts.Token);

        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var clientPipe = new WebSocketDuplexPipe(client);
        var clientSession = new MuxSession(clientPipe, isServer: false, NullLogger.Instance, (stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var clientRun = clientSession.RunAsync();
        var serverSession = await serverSessionReady.Task.WaitAsync(Timeout);

        try
        {
            await using var serverStream = await serverSession.OpenStreamAsync(
                new SwapRequest { Id = "ws-big", Host = "10.0.0.8", Port = 80 }, cts.Token);

            var remote = await accepted.Task.WaitAsync(Timeout);

            // 1MB：跨越 256KB 单流窗口多次，同时会产生 60+ 个 16KB 数据帧
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024);

            var writeTask = Task.Run(async () =>
            {
                const int chunkSize = 16 * 1024;
                for (var offset = 0; offset < payload.Length; offset += chunkSize)
                {
                    var size = Math.Min(chunkSize, payload.Length - offset);
                    await serverStream.WriteAsync(payload.AsMemory(offset, size), cts.Token);
                }
            }, cts.Token);

            var readTask = TestPipes.ReadExactAsync(remote.Input, payload.Length, cts.Token);

            await Task.WhenAll(writeTask, readTask);

            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(payload)),
                Convert.ToHexString(SHA256.HashData(await readTask)));
        }
        finally
        {
            await serverSession.DisposeAsync();
            await clientSession.DisposeAsync();
            await Task.WhenAny(clientRun, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task 接收的消息被摊平成连续字节流()
    {
        var serverSocketReady = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await WsServer.StartAsync(async socket =>
        {
            serverSocketReady.TrySetResult(socket);

            // 连续发三条消息，接收侧应当读到一条连续的 "abc"
            await socket.SendAsync("a"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.SendAsync("b"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.SendAsync("c"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);

            await finish.Task.WaitAsync(TimeSpan.FromSeconds(20));
        });

        using var cts = new CancellationTokenSource(Timeout);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(server.Uri, cts.Token);
        await serverSocketReady.Task.WaitAsync(Timeout);

        await using var pipe = new WebSocketDuplexPipe(client);

        var bytes = await TestPipes.ReadExactAsync(pipe.Input, 3, cts.Token);

        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(bytes));

        finish.TrySetResult("done");
    }

    [Fact]
    public async Task 发送侧一次Flush对应一条WebSocket消息()
    {
        var serverSocketReady = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await WsServer.StartAsync(async socket =>
        {
            serverSocketReady.TrySetResult(socket);

            var buffer = new byte[64 * 1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            received.TrySetResult(result.Count);
        });

        using var cts = new CancellationTokenSource(Timeout);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(server.Uri, cts.Token);
        await serverSocketReady.Task.WaitAsync(Timeout);

        await using var pipe = new WebSocketDuplexPipe(client);

        // 帧头 + 帧体分多次写入，最后只 Flush 一次
        var output = pipe.Output;

        var headerSpan = output.GetSpan(MuxFrame.HeaderSize);
        MuxFrame.WriteHeader(headerSpan, MuxFrameType.Data, 1, 100);
        output.Advance(MuxFrame.HeaderSize);

        output.GetSpan(100);
        output.Advance(100);

        await output.FlushAsync(cts.Token);

        var count = await received.Task.WaitAsync(Timeout);

        // 一次 Flush 只产生一条消息：9 字节帧头 + 100 字节帧体
        Assert.Equal(MuxFrame.HeaderSize + 100, count);
    }
}
