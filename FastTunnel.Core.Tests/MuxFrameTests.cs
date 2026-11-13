// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using FastTunnel.Core.Mux;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 多路复用帧头。帧格式完全由字节序决定，所以这里既验往返，也逐字节验大端布局——
/// 大小端搞反时客户端与服务端是能「互通」的（都错），但会对不上 yamux 等外部实现的字节流。
/// </summary>
public class MuxFrameTests
{
    [Fact]
    public void HeaderSize_是9字节()
        => Assert.Equal(9, MuxFrame.HeaderSize);

    [Theory]
    [InlineData(MuxFrameType.Syn, 1u, 0)]
    [InlineData(MuxFrameType.Data, 3u, 16)]
    [InlineData(MuxFrameType.Fin, 5u, 0)]
    [InlineData(MuxFrameType.Rst, 7u, 0)]
    [InlineData(MuxFrameType.WindowUpdate, 9u, 4)]
    [InlineData(MuxFrameType.Ping, 0u, 0)]
    [InlineData(MuxFrameType.Pong, 0u, 0)]
    [InlineData(MuxFrameType.GoAway, 0u, 0)]
    [InlineData(MuxFrameType.Data, uint.MaxValue, MuxFrame.MaxPayload)]
    public void 帧头往返(MuxFrameType type, uint streamId, int length)
    {
        var buffer = new byte[MuxFrame.HeaderSize];
        MuxFrame.WriteHeader(buffer, type, streamId, length);

        var (actualType, actualId, actualLength) = MuxFrame.ReadHeader(buffer);

        Assert.Equal(type, actualType);
        Assert.Equal(streamId, actualId);
        Assert.Equal(length, actualLength);
    }

    [Fact]
    public void 帧头按大端写入()
    {
        var buffer = new byte[MuxFrame.HeaderSize];

        // 0x01020304：大端是 01 02 03 04，小端是 04 03 02 01
        MuxFrame.WriteHeader(buffer, MuxFrameType.Data, 0x01020304, 0x0A0B0C0D);

        Assert.Equal((byte)MuxFrameType.Data, buffer[0]);
        Assert.Equal(0x01, buffer[1]);
        Assert.Equal(0x02, buffer[2]);
        Assert.Equal(0x03, buffer[3]);
        Assert.Equal(0x04, buffer[4]);
        Assert.Equal(0x0A, buffer[5]);
        Assert.Equal(0x0B, buffer[6]);
        Assert.Equal(0x0C, buffer[7]);
        Assert.Equal(0x0D, buffer[8]);
    }

    [Fact]
    public void 超长帧被拒绝()
    {
        var buffer = new byte[MuxFrame.HeaderSize];
        MuxFrame.WriteHeader(buffer, MuxFrameType.Data, 1, MuxFrame.MaxPayload + 1);

        // 长度字段来自对端，一个坏帧不能把进程撑爆
        var ex = Assert.Throws<InvalidOperationException>(() => MuxFrame.ReadHeader(buffer));
        Assert.Contains("非法帧长度", ex.Message);
    }

    [Fact]
    public void 恰好等于上限的帧允许通过()
    {
        var buffer = new byte[MuxFrame.HeaderSize];
        MuxFrame.WriteHeader(buffer, MuxFrameType.Data, 1, MuxFrame.MaxPayload);

        var (_, _, length) = MuxFrame.ReadHeader(buffer);

        Assert.Equal(MuxFrame.MaxPayload, length);
    }

    [Fact]
    public void 窗口常量之间必须自洽()
    {
        // 单流内存上限 = 初始窗口；会话上限 = 窗口 × 并发流数
        Assert.Equal(256 * 1024, MuxFrame.InitialWindow);
        Assert.Equal(16 * 1024, MuxFrame.MaxPayload);

        // 单帧不能超过窗口，否则「发满一个窗口」这件事就没法用整帧表达
        Assert.True(MuxFrame.InitialWindow > MuxFrame.MaxPayload);
    }
}
