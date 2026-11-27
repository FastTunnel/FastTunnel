// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SessionPair = FastTunnel.Core.Tests.Support.TestMuxPair;

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

    // ---------------- 帧解析（读取循环改为「一次 ReadAsync 解多帧」后的回归） ----------------

    [Fact]
    public async Task 帧头被拆到多个管道段也能解析()
    {
        var accepted = new TaskCompletionSource<(MuxStream Stream, SwapRequest Request)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, request) =>
        {
            accepted.TrySetResult((stream, request));
            return Task.CompletedTask;
        });

        var target = new SwapRequest { Id = "split", Host = "10.0.0.9", Port = 8081, Proto = "tcp" };
        var json = JsonSerializer.SerializeToUtf8Bytes(target, ControlProtocol.JsonOptions);
        var syn = Frame(MuxFrameType.Syn, 2, json);

        using var cts = new CancellationTokenSource(Timeout);

        // 故意在 9 字节帧头中间断开：解析器必须能跨段读出头，而不是要求帧头连续
        await TestPipes.WriteAsync(pair.ServerSide, syn.AsMemory(0, 3), cts.Token);
        await TestPipes.WriteAsync(pair.ServerSide, syn.AsMemory(3, 4), cts.Token);
        await TestPipes.WriteAsync(pair.ServerSide, syn.AsMemory(7), cts.Token);

        var (stream, actual) = await accepted.Task.WaitAsync(Timeout);

        Assert.Equal("10.0.0.9", actual.Host);
        Assert.Equal(8081, actual.Port);
        Assert.Equal(2u, stream.Id);
    }

    [Fact]
    public async Task 帧体分多次到达时按长度拼齐再交给流()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var target = new SwapRequest { Id = "body", Host = "10.0.0.9", Port = 8082, Proto = "tcp" };
        var json = JsonSerializer.SerializeToUtf8Bytes(target, ControlProtocol.JsonOptions);

        using var cts = new CancellationTokenSource(Timeout);

        await TestPipes.WriteAsync(pair.ServerSide, Frame(MuxFrameType.Syn, 2, json), cts.Token);

        var stream = await accepted.Task.WaitAsync(Timeout);
        var payload = RandomNumberGenerator.GetBytes(5000);
        var data = Frame(MuxFrameType.Data, stream.Id, payload);

        // 三段写入：帧头之后、帧体中间各断一次，读取循环必须等够长度才往下走
        await TestPipes.WriteAsync(pair.ServerSide, data.AsMemory(0, 100), cts.Token);
        await TestPipes.WriteAsync(pair.ServerSide, data.AsMemory(100, 3000), cts.Token);
        await TestPipes.WriteAsync(pair.ServerSide, data.AsMemory(3100), cts.Token);

        Assert.Equal(payload, await TestPipes.ReadExactAsync(stream.Input, payload.Length, cts.Token));
    }

    [Fact]
    public async Task 窗口更新帧的4字节负载跨段时不会拖垮会话()
    {
        // 回归：窗口更新只有 4 字节负载，很容易正好跨在两个管道段之间。
        // 早期实现直接对 FirstSpan 读 4 字节，跨段时抛 ArgumentOutOfRange，
        // 整条多路复用会话（以及上面所有转发连接）一起断掉。
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        var target = new SwapRequest { Id = "win", Host = "10.0.0.9", Port = 8083, Proto = "tcp" };
        var json = JsonSerializer.SerializeToUtf8Bytes(target, ControlProtocol.JsonOptions);

        using var cts = new CancellationTokenSource(Timeout);

        await TestPipes.WriteAsync(pair.ServerSide, Frame(MuxFrameType.Syn, 2, json), cts.Token);
        var stream = await accepted.Task.WaitAsync(Timeout);

        var before = stream.SendWindow;
        var delta = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(delta, 12_345);
        var update = Frame(MuxFrameType.WindowUpdate, stream.Id, delta);

        // 帧头 9 字节 + 负载前 2 字节一段，负载后 2 字节另一段
        await TestPipes.WriteAsync(pair.ServerSide, update.AsMemory(0, 11), cts.Token);
        await TestPipes.WriteAsync(pair.ServerSide, update.AsMemory(11), cts.Token);

        await WaitUntilAsync(() => stream.SendWindow == before + 12_345, TimeSpan.FromSeconds(5));

        // 会话必须还活着，流也必须还能用
        Assert.False(pair.Client.IsClosed);

        var payload = new byte[] { 7, 8, 9 };
        await TestPipes.WriteAsync(pair.ServerSide, Frame(MuxFrameType.Data, stream.Id, payload), cts.Token);
        Assert.Equal(payload, await TestPipes.ReadExactAsync(stream.Input, payload.Length, cts.Token));
    }

    [Fact]
    public async Task UDP数据报写入_跨多个MUX帧后仍按长度前缀还原()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        await using var serverStream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "udp", Host = "10.0.0.8", Port = 5353, Proto = "udp" }, TestToken());
        var clientStream = await accepted.Task.WaitAsync(Timeout);

        // 32KB 负载会被拆成多个 16KB MUX 帧，但 UDP 接收端应看到连续的 [长度][负载]。
        var payload = new byte[32 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await serverStream.WriteDatagramAsync(payload, TestToken());

        var framed = await TestPipes.ReadExactAsync(clientStream.Input, payload.Length + 2, TestToken());
        Assert.Equal((byte)(payload.Length >> 8), framed[0]);
        Assert.Equal((byte)payload.Length, framed[1]);
        Assert.Equal(payload, framed.AsMemory(2).ToArray());
    }

    [Fact]
    public async Task StreamToPipe_直接写入MuxWriter时数据完整()
    {
        var accepted = new TaskCompletionSource<MuxStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = new SessionPair((stream, _) =>
        {
            accepted.TrySetResult(stream);
            return Task.CompletedTask;
        });

        await using var serverStream = await pair.Server.OpenStreamAsync(
            new SwapRequest { Id = "pump", Host = "10.0.0.8", Port = 8080 }, TestToken());
        var clientStream = await accepted.Task.WaitAsync(Timeout);

        var payload = RandomNumberGenerator.GetBytes(128 * 1024);
        using var source = new MemoryStream(payload);

        await PipePumps.StreamToPipeAsync(source, serverStream.Output, TestToken());

        Assert.Equal(payload, await TestPipes.ReadExactAsync(clientStream.Input, payload.Length, TestToken()));
    }

    private static byte[] Frame(MuxFrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[MuxFrame.HeaderSize + payload.Length];
        MuxFrame.WriteHeader(buffer, type, streamId, payload.Length);
        payload.CopyTo(buffer.AsSpan(MuxFrame.HeaderSize));
        return buffer;
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
