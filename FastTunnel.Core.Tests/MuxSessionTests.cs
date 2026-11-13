// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 多路复用数据面。两端各一个 <see cref="MuxSession"/>，中间用一对内存 Pipe 代替 TCP，
/// 因此不需要端口、不会互相干扰。覆盖的是最容易回归的几条路径：
/// SYN 携带目标地址、双向数据、半关闭、RST、流控（超过单流窗口的大流量）、streamId 号段。
/// </summary>
public class MuxSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static CancellationToken TestToken() => new CancellationTokenSource(Timeout).Token;

    private sealed class SessionPair : IAsyncDisposable
    {
        public SessionPair(Func<MuxStream, SwapRequest, Task>? onAccept = null)
        {
            var (serverPipe, clientPipe) = TestPipes.CreatePair();

            ServerSide = serverPipe;
            ClientSide = clientPipe;

            Server = new MuxSession(serverPipe, isServer: true, NullLogger.Instance);
            Client = new MuxSession(clientPipe, isServer: false, NullLogger.Instance, onAccept);

            ServerRun = Server.RunAsync();
            ClientRun = Client.RunAsync();
        }

        public IDuplexPipe ServerSide { get; }

        public IDuplexPipe ClientSide { get; }

        public MuxSession Server { get; }

        public MuxSession Client { get; }

        public Task ServerRun { get; }

        public Task ClientRun { get; }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Client.DisposeAsync();

            await Task.WhenAny(Task.WhenAll(ServerRun, ClientRun), Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Syn携带目标地址_客户端按地址开流()
    {
        var accepted = new TaskCompletionSource<(MuxStream Stream, SwapRequest Request)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, request) =>
        {
            accepted.TrySetResult((stream, request));
            return Task.CompletedTask;
        });

        var target = new SwapRequest { Id = "session-1", Host = "10.0.0.8", Port = 8080, Proto = "tcp" };

        await using var serverStream = await pair.Server.OpenStreamAsync(target, TestToken());

        var (remoteStream, actual) = await accepted.Task.WaitAsync(Timeout);

        Assert.Equal("10.0.0.8", actual.Host);
        Assert.Equal(8080, actual.Port);
        Assert.Equal("tcp", actual.Proto);
        Assert.Equal("session-1", actual.Id);
        Assert.Equal(serverStream.Id, remoteStream.Id);
    }

    [Fact]
    public async Task 双向数据传输()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        await using var serverStream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "s", Host = "10.0.0.8", Port = 80 }, TestToken());

        var clientStream = await accepted.Task.WaitAsync(Timeout);

        using var cts = new CancellationTokenSource(Timeout);

        // 下行：服务端写 → 客户端读
        var downstream = new byte[] { 1, 2, 3, 4, 5 };
        await serverStream.WriteAsync(downstream, cts.Token);
        Assert.Equal(downstream, await TestPipes.ReadExactAsync(clientStream.Input, downstream.Length, cts.Token));

        // 上行：客户端写 → 服务端读
        var upstream = new byte[] { 9, 8, 7 };
        await clientStream.WriteAsync(upstream, cts.Token);
        Assert.Equal(upstream, await TestPipes.ReadExactAsync(serverStream.Input, upstream.Length, cts.Token));
    }

    [Fact]
    public async Task 多流并发互不串扰()
    {
        var accepted = new System.Collections.Concurrent.ConcurrentDictionary<uint, MuxStream>();

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted[stream.Id] = stream;
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource(Timeout);

        const int count = 8;
        var streams = new MuxStream[count];

        for (var i = 0; i < count; i++)
        {
            streams[i] = await pair.Server.OpenStreamAsync(
                new SwapRequest { Id = $"s{i}", Host = "10.0.0.8", Port = 8000 + i }, cts.Token);
        }

        // SYN 是异步送达的，OpenStreamAsync 返回时对端未必已经建流
        await WaitUntilAsync(() => accepted.Count == count, TimeSpan.FromSeconds(5));

        // 每条流各写自己的编号，验证帧不会串到别的流上
        for (var i = 0; i < count; i++)
        {
            await streams[i].WriteAsync(BitConverter.GetBytes(i), cts.Token);
        }

        for (var i = 0; i < count; i++)
        {
            var remote = accepted[streams[i].Id];
            var payload = await TestPipes.ReadExactAsync(remote.Input, 4, cts.Token);
            Assert.Equal(i, BitConverter.ToInt32(payload));
        }

        Assert.Equal(count, pair.Client.StreamCount);

        foreach (var stream in streams)
        {
            await stream.DisposeAsync();
        }
    }

    [Fact]
    public async Task 优雅关闭时先发Fin_对端读到EOF且不丢尾部数据()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var serverStream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "s", Host = "10.0.0.8", Port = 80 }, TestToken());

        var clientStream = await accepted.Task.WaitAsync(Timeout);

        using var cts = new CancellationTokenSource(Timeout);

        // 最后一批数据必须被发出去后再 EOF——HTTP 响应体被截断就是这么来的
        var tail = RandomNumberGenerator.GetBytes(1000);
        await serverStream.WriteAsync(tail, cts.Token);
        await serverStream.DisposeAsync();

        Assert.Equal(tail, await TestPipes.ReadExactAsync(clientStream.Input, tail.Length, cts.Token));

        using var readCts = new CancellationTokenSource(Timeout);
        var eofRead = await clientStream.ReadAsync(new byte[16], readCts.Token);
        Assert.Equal(0, eofRead);
    }

    [Fact]
    public async Task 服务端不接收客户端发起的流_回Rst()
    {
        await using var pair = new SessionPair();

        using var cts = new CancellationTokenSource(Timeout);

        var clientStream = await pair.Client.OpenStreamAsync(
            new SwapRequest { Id = "c", Host = "10.0.0.8", Port = 80 }, cts.Token);

        // 客户端发起流（stcp 才会用到）当前不被支持，服务端回 RST 后本端流被回收
        await WaitUntilAsync(() => pair.Client.StreamCount == 0, TimeSpan.FromSeconds(5));

        // RST 之后读端要么 EOF、要么报错，总之不能再有数据
        var terminated = false;
        try
        {
            var read = await clientStream.ReadAsync(new byte[8], new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            terminated = read == 0;
        }
        catch (Exception)
        {
            terminated = true;
        }

        Assert.True(terminated);
    }

    [Fact]
    public async Task StreamId按奇偶分段_服务端奇数客户端偶数()
    {
        var accepted = new System.Collections.Concurrent.ConcurrentDictionary<uint, MuxStream>();

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted[stream.Id] = stream;
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var serverTarget = new SwapRequest { Id = "s", Host = "10.0.0.8", Port = 80 };
        var clientTarget = new SwapRequest { Id = "c", Host = "10.0.0.8", Port = 80 };

        var s1 = await pair.Server.OpenStreamAsync(serverTarget, cts.Token);
        var s2 = await pair.Server.OpenStreamAsync(serverTarget, cts.Token);
        var c1 = await pair.Client.OpenStreamAsync(clientTarget, cts.Token);
        var c2 = await pair.Client.OpenStreamAsync(clientTarget, cts.Token);

        Assert.Equal(1u, s1.Id);
        Assert.Equal(3u, s2.Id);
        Assert.Equal(2u, c1.Id);
        Assert.Equal(4u, c2.Id);

        // 服务端奇数、客户端偶数，两条号段互不重叠
        Assert.Equal(1u, s1.Id % 2);
        Assert.Equal(1u, s2.Id % 2);
        Assert.Equal(0u, c1.Id % 2);
        Assert.Equal(0u, c2.Id % 2);
    }

    [Fact]
    public async Task 大流量超过单流窗口仍能完整传输_流控不死锁()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var serverStream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "big", Host = "10.0.0.8", Port = 80 }, TestToken());

        var clientStream = await accepted.Task.WaitAsync(Timeout);

        // 4MB 远超单流初始窗口（256KB），必须靠 WindowUpdate 才能走完；写或读任一侧卡住都会超时
        var payload = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeTask = Task.Run(async () =>
        {
            const int chunkSize = 16 * 1024;
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                var size = Math.Min(chunkSize, payload.Length - offset);
                await serverStream.WriteAsync(payload.AsMemory(offset, size), cts.Token);
            }
        }, cts.Token);

        var readTask = TestPipes.ReadExactAsync(clientStream.Input, payload.Length, cts.Token);

        await Task.WhenAll(writeTask, readTask);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), Convert.ToHexString(SHA256.HashData(await readTask)));
    }

    [Fact]
    public async Task 大流量反向同样走通()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        await using var serverStream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "up", Host = "10.0.0.8", Port = 80 }, TestToken());

        var clientStream = await accepted.Task.WaitAsync(Timeout);

        var payload = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeTask = Task.Run(async () =>
        {
            const int chunkSize = 16 * 1024;
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                var size = Math.Min(chunkSize, payload.Length - offset);
                await clientStream.WriteAsync(payload.AsMemory(offset, size), cts.Token);
            }
        }, cts.Token);

        var readTask = TestPipes.ReadExactAsync(serverStream.Input, payload.Length, cts.Token);

        await Task.WhenAll(writeTask, readTask);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), Convert.ToHexString(SHA256.HashData(await readTask)));
    }

    [Fact]
    public async Task Ping能让链路保活_不影响数据面()
    {
        await using var pair = new SessionPair((_, _) => Task.CompletedTask);

        using var cts = new CancellationTokenSource(Timeout);

        await pair.Server.SendPingAsync(cts.Token);

        // 对端回 Pong 后会话仍然可用
        await using var stream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "after-ping", Host = "10.0.0.8", Port = 80 }, cts.Token);

        Assert.False(pair.Server.IsClosed);
        Assert.False(pair.Client.IsClosed);
        Assert.Equal(1, pair.Server.StreamCount);
    }

    [Fact]
    public async Task 会话关闭后不再接受新流()
    {
        var (serverPipe, clientPipe) = TestPipes.CreatePair();
        var server = new MuxSession(serverPipe, isServer: true, NullLogger.Instance);
        var client = new MuxSession(clientPipe, isServer: false, NullLogger.Instance);

        await server.DisposeAsync();
        await client.DisposeAsync();

        Assert.True(server.IsClosed);

        await Assert.ThrowsAsync<IOException>(() => server.OpenStreamAsync(
            new SwapRequest { Id = "x", Host = "10.0.0.8", Port = 80 }, CancellationToken.None));
    }

    [Fact]
    public async Task 对端断开时本端会话自动收敛()
    {
        var (serverPipe, clientPipe) = TestPipes.CreatePair();
        var server = new MuxSession(serverPipe, isServer: true, NullLogger.Instance);
        var client = new MuxSession(clientPipe, isServer: false, NullLogger.Instance);

        var serverRun = server.RunAsync();
        var clientRun = client.RunAsync();

        // 对端主动关闭 = 客户端会话发 GoAway 后断开
        await client.DisposeAsync();

        await serverRun.WaitAsync(TimeSpan.FromSeconds(5));
        await clientRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(server.IsClosed);
        Assert.True(client.IsClosed);
    }

    // ---------------- ReadExactAsync ----------------

    [Fact]
    public async Task ReadExactAsync_分片到达也能读满()
    {
        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(Timeout);
        var target = new byte[10];

        var readTask = MuxSession.ReadExactAsync(pipe.Reader, target, cts.Token);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await pipe.Writer.WriteAsync(new byte[] { 4, 5 }, cts.Token);
        await pipe.Writer.WriteAsync(new byte[] { 6, 7, 8, 9, 10 }, cts.Token);

        await readTask.AsTask().WaitAsync(Timeout);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, target);
    }

    [Fact]
    public async Task ReadExactAsync_数据不足就结束会报错而不是静默返回()
    {
        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(Timeout);

        var readTask = MuxSession.ReadExactAsync(pipe.Reader, new byte[10], cts.Token);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        pipe.Writer.Complete();

        await Assert.ThrowsAsync<EndOfStreamException>(() => readTask.AsTask());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("等待条件超时");
            }

            await Task.Delay(20);
        }
    }
}
