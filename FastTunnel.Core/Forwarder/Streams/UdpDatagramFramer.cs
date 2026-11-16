// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// UDP 数据报在隧道流上的线格式辅助：[2 字节大端长度][负载]。
///
/// 线格式不变；这里只负责把「池化缓冲 / 管道切片」安全地接进去：
/// - 写：小包仍拼成一个缓冲一次写，避免两次 flush；大包且对端是管道时
///   头和负载分两次写，省掉一次整包拷贝。
/// - 读：管道里是单段时直接把切片交给 UDP socket；跨段才借池化缓冲拼连续块。
/// </summary>
internal static class UdpDatagramFramer
{
    internal const int MaxDatagramSize = 65507;

    /// <summary>
    /// 小于该长度时拼包一次写更划算；大于该长度时省一次全量 memcpy 更重要。
    /// </summary>
    private const int CombineThreshold = 8 * 1024;

    internal static async ValueTask WriteAsync(
        Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxDatagramSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "UDP 数据报超过最大长度");
        }

        // 生产环境里 UDP 流会被 TrafficMeter 包一层 MeteredStream，
        // 直接判断 IDuplexPipe 会漏掉底层管道；IDatagramWriter 由装饰器透传，
        // 让 MuxStream 能把长度前缀和负载一起写进 MUX 帧，少一次全量拷贝。
        if (stream is IDatagramWriter datagram)
        {
            await datagram.WriteDatagramAsync(payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 非 Mux 的裸管道：大包直接写负载，让帧封装只拷一次；
        // 小包仍拼成一个缓冲一次写，避免多一次 flush。
        if (payload.Length > CombineThreshold && stream is IDuplexPipe pipe)
        {
            var header = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)payload.Length);
                await pipe.Output.WriteAsync(header.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                await pipe.Output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }

            return;
        }

        var frame = ArrayPool<byte>.Shared.Rent(2 + payload.Length);
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
            payload.Span.CopyTo(frame.AsSpan(2, payload.Length));
            await stream.WriteAsync(frame.AsMemory(0, 2 + payload.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>
    /// 从管道缓冲里解析一条完整数据报。返回的 payload 可能是管道切片，
    /// 也可能是 <paramref name="scratch"/> 的切片；调用方必须在发送完成前不要推进管道。
    /// </summary>
    internal static bool TryRead(
        ReadOnlySequence<byte> buffer,
        byte[] scratch,
        out ReadOnlyMemory<byte> payload,
        out long totalLength)
    {
        payload = default;
        totalLength = 0;

        if (buffer.Length < 2)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryRead(out var high) || !reader.TryRead(out var low))
        {
            return false;
        }

        var length = (high << 8) | low;
        if (length > MaxDatagramSize)
        {
            throw new InvalidDataException($"非法 UDP 数据报长度 {length}");
        }

        if (buffer.Length - 2 < length)
        {
            return false;
        }

        totalLength = 2L + length;
        var body = buffer.Slice(2, length);
        if (body.IsSingleSegment)
        {
            payload = body.First;
        }
        else
        {
            body.CopyTo(scratch);
            payload = scratch.AsMemory(0, length);
        }

        return true;
    }

    /// <summary>非管道流的兼容读取路径（TLS / 普通 NetworkStream）。</summary>
    internal static async ValueTask<bool> ReadExactAsync(
        Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await stream.ReadAsync(destination.Slice(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
