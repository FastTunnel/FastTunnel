// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers.Binary;

namespace FastTunnel.Core.Mux;

/// <summary>
/// 多路复用帧类型。
/// </summary>
public enum MuxFrameType : byte
{
    /// <summary>打开一条逻辑流，payload 为 JSON 化的 <see cref="Protocol.SwapRequest"/>（目标地址）。</summary>
    Syn = 1,

    /// <summary>流数据。</summary>
    Data = 2,

    /// <summary>发送方本方向不再有数据（半关闭）。</summary>
    Fin = 3,

    /// <summary>异常中止该流。</summary>
    Rst = 4,

    /// <summary>窗口更新，payload 为 4 字节大端增量。</summary>
    WindowUpdate = 5,

    /// <summary>链路探活。</summary>
    Ping = 6,

    /// <summary>链路探活应答。</summary>
    Pong = 7,

    /// <summary>整个会话即将关闭。</summary>
    GoAway = 8,
}

/// <summary>
/// 帧格式：<c>[1 字节类型][4 字节 streamId][4 字节 payload 长度][payload]</c>，全部大端。
/// 与 yamux 同构，但字段更少：本项目的 streamId 由服务端/客户端各自的奇偶号段分配，
/// 单连接内唯一，不需要额外的 session id。
/// </summary>
public static class MuxFrame
{
    public const int HeaderSize = 9;

    /// <summary>
    /// 单帧最大 payload。帧越小，多流交织越公平、队头阻塞越短；越大，头部开销占比越低。
    /// 16KB 与 Pipe 默认段大小同量级，一次拷贝即可装满。
    /// </summary>
    public const int MaxPayload = 16 * 1024;

    /// <summary>
    /// 每条流的初始接收窗口。发送方未收到窗口更新前最多只能发这么多字节，
    /// 因此单流的内存占用上限就是这个值——流控本质上是内存保护。
    /// </summary>
    public const int InitialWindow = 256 * 1024;

    /// <summary>
    /// 单会话最大并发流数。用于兜底内存：<see cref="InitialWindow"/> × 该值 是理论峰值。
    /// </summary>
    public const int MaxStreamCount = 1024;

    public static void WriteHeader(Span<byte> buffer, MuxFrameType type, uint streamId, int length)
    {
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(1, 4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(5, 4), (uint)length);
    }

    public static (MuxFrameType Type, uint StreamId, int Length) ReadHeader(ReadOnlySpan<byte> buffer)
    {
        var type = (MuxFrameType)buffer[0];
        var streamId = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(1, 4));
        var length = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(5, 4));

        // 长度字段来自对端，必须校验，否则一个坏帧就能让进程 OOM。
        if (length > MaxPayload)
        {
            throw new InvalidOperationException($"非法帧长度 {length}");
        }

        return (type, streamId, (int)length);
    }
}
