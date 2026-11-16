// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Refs;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// UDP 帧辅助：线格式不变，重点验证单段直切、跨段拼接和管道大包不整包中转。
/// </summary>
public class UdpDatagramFramerTests
{
    [Fact]
    public async Task WriteAsync_小包拼成单帧写入普通流()
    {
        using var stream = new MemoryStream();

        await UdpDatagramFramer.WriteAsync(stream, new byte[] { 1, 2, 3 }, default);

        Assert.Equal(new byte[] { 0, 3, 1, 2, 3 }, stream.ToArray());
    }

    [Fact]
    public async Task WriteAsync_大包走低拷贝管道写且可解析回原负载()
    {
        var payload = new byte[16 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var tunnel = new Pipe();
        using var stream = new DuplexPipeStream(PipeReader.Create(Stream.Null), tunnel.Writer);

        await UdpDatagramFramer.WriteAsync(stream, payload, default);

        var result = await tunnel.Reader.ReadAsync();
        var scratch = new byte[UdpDatagramFramer.MaxDatagramSize];
        Assert.True(UdpDatagramFramer.TryRead(result.Buffer, scratch, out var parsed, out var total));
        Assert.Equal(2 + payload.Length, total);
        Assert.Equal(payload, parsed.ToArray());
        tunnel.Reader.AdvanceTo(result.Buffer.GetPosition(total));
    }

    [Fact]
    public void TryRead_单段直接切片不借scratch()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[] { 0, 3, 1, 2, 3 });
        var scratch = new byte[UdpDatagramFramer.MaxDatagramSize];
        Array.Fill(scratch, (byte)0xCC);

        Assert.True(UdpDatagramFramer.TryRead(buffer, scratch, out var payload, out var total));
        Assert.Equal(5, total);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        Assert.All(scratch.AsSpan(0, 3).ToArray(), b => Assert.Equal((byte)0xCC, b));
    }

    [Fact]
    public void TryRead_跨段数据借scratch拼成连续内存()
    {
        var tail = new SequenceSegment(new byte[] { 2, 3 });
        var head = new SequenceSegment(new byte[] { 0, 3, 1 }, tail);
        var buffer = new ReadOnlySequence<byte>(head, 0, tail, 2);
        var scratch = new byte[UdpDatagramFramer.MaxDatagramSize];

        Assert.True(UdpDatagramFramer.TryRead(buffer, scratch, out var payload, out var total));
        Assert.Equal(5, total);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, scratch.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void TryRead_数据未到齐时返回false且不推进()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[] { 0, 3, 1 });
        var scratch = new byte[UdpDatagramFramer.MaxDatagramSize];

        Assert.False(UdpDatagramFramer.TryRead(buffer, scratch, out _, out var total));
        Assert.Equal(0, total);
    }

    [Fact]
    public void TryRead_非法长度抛异常()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[] { 0xFF, 0xFF, 1 });
        var scratch = new byte[UdpDatagramFramer.MaxDatagramSize];

        Assert.Throws<InvalidDataException>(() =>
            UdpDatagramFramer.TryRead(buffer, scratch, out _, out _));
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory, SequenceSegment? next = null)
        {
            Memory = memory;
            Next = next;
            if (next != null)
            {
                next.RunningIndex = RunningIndex + memory.Length;
            }
        }
    }
}
