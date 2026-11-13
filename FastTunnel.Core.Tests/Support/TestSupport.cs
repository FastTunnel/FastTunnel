// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Tests.Support;

/// <summary>最简 <see cref="IDuplexPipe"/> 实现，用于把两段 Pipe 拼成一条「内存连接」。</summary>
public sealed class TestDuplexPipe : IDuplexPipe
{
    public TestDuplexPipe(PipeReader input, PipeWriter output)
    {
        Input = input;
        Output = output;
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }
}

public static class TestPipes
{
    /// <summary>
    /// 造一对互连的管道：写 A 的 Output 能从 B 的 Input 读到，反之亦然。
    /// 相当于一条没有内核参与的 TCP 连接，用来测多路复用会话。
    /// </summary>
    public static (IDuplexPipe A, IDuplexPipe B) CreatePair()
    {
        var aToB = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var bToA = new Pipe(new PipeOptions(useSynchronizationContext: false));

        return (new TestDuplexPipe(aToB.Reader, bToA.Writer),
                new TestDuplexPipe(bToA.Reader, aToB.Writer));
    }

    /// <summary>对端管道容量放大，避免测试搬运大块数据时被 Pipe 自身的阈值阻塞。</summary>
    public static (IDuplexPipe A, IDuplexPipe B) CreateLargePair()
    {
        var options = new PipeOptions(
            pauseWriterThreshold: 8 * 1024 * 1024,
            resumeWriterThreshold: 4 * 1024 * 1024,
            minimumSegmentSize: 4096,
            useSynchronizationContext: false);

        var aToB = new Pipe(options);
        var bToA = new Pipe(options);

        return (new TestDuplexPipe(aToB.Reader, bToA.Writer),
                new TestDuplexPipe(bToA.Reader, aToB.Writer));
    }

    /// <summary>
    /// 单端自环：Input 与 Output 指向同一条 Pipe 的两端，
    /// 写进去的字节立刻能从自己的 Input 读到。
    ///
    /// 注意与 <see cref="CreatePair"/> 的区别：Pair 是「对端」语义，写 A 的 Output 只能到 B 的 Input，
    /// A 自己读不到自己的写。只测连接一侧（例如连接层中间件）时要用自环。
    /// </summary>
    public static IDuplexPipe CreateSelfLoop()
    {
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        return new TestDuplexPipe(pipe.Reader, pipe.Writer);
    }

    public static async Task WriteAsync(IDuplexPipe pipe, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await pipe.Output.WriteAsync(payload, cancellationToken);
    }

    public static Task WriteAsync(IDuplexPipe pipe, string text, CancellationToken cancellationToken = default)
        => WriteAsync(pipe, System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>精确读取 count 字节；对端提前结束则抛 <see cref="EndOfStreamException"/>。</summary>
    public static async Task<byte[]> ReadExactAsync(PipeReader reader, int count, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[count];
        var filled = 0;

        while (filled < count)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var sequence = result.Buffer;

            if (sequence.IsEmpty)
            {
                reader.AdvanceTo(sequence.Start, sequence.End);

                if (result.IsCompleted)
                {
                    throw new EndOfStreamException($"期望 {count} 字节，实际只收到 {filled}");
                }

                continue;
            }

            var take = (int)Math.Min(sequence.Length, count - filled);
            sequence.Slice(0, take).CopyTo(buffer.AsSpan(filled));
            reader.AdvanceTo(sequence.GetPosition(take));
            filled += take;
        }

        return buffer;
    }

    /// <summary>读到 EOF 为止的全部内容。</summary>
    public static async Task<byte[]> ReadToEndAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var sequence = result.Buffer;

            foreach (var segment in sequence)
            {
                ms.Write(segment.Span);
            }

            reader.AdvanceTo(sequence.End);

            if (result.IsCompleted)
            {
                return ms.ToArray();
            }
        }
    }

    public static async Task<string> ReadToEndAsStringAsync(PipeReader reader, CancellationToken cancellationToken = default)
        => System.Text.Encoding.UTF8.GetString(await ReadToEndAsync(reader, cancellationToken));
}

/// <summary>可用的 <see cref="ConnectionContext"/>，Transport 走内存管道，不涉及真实 socket。</summary>
public sealed class TestConnectionContext : ConnectionContext
{
    private readonly CancellationTokenSource _closed = new();

    public TestConnectionContext(IDuplexPipe transport)
    {
        Transport = transport;
        ConnectionClosed = _closed.Token;
    }

    public override string ConnectionId { get; set; } = Guid.NewGuid().ToString("N");

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

    public override IDuplexPipe Transport { get; set; }

    public override CancellationToken ConnectionClosed { get; set; }

    public override System.Net.EndPoint? RemoteEndPoint { get; set; } =
        new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 54321);

    public override void Abort() => _closed.Cancel();
}

/// <summary>固定值的 <see cref="IOptionsMonitor{T}"/>，避免为测试引入 DI 容器。</summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>内容根可指定的 <see cref="IHostEnvironment"/>，用于证书落盘路径测试。</summary>
public sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string contentRootPath = "")
    {
        ContentRootPath = contentRootPath;
    }

    public string EnvironmentName { get; set; } = "Development";

    public string ApplicationName { get; set; } = "FastTunnel.Core.Tests";

    public string ContentRootPath { get; set; }

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>每个用例一个独立临时目录，用完即删。</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fasttunnel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Path;
        parts.CopyTo(all, 1);
        return System.IO.Path.Combine(all);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Windows 上证书句柄可能还没释放，残留一个临时目录不影响测试结论
        }
    }
}
