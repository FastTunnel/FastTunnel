// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// socket / 流 → 管道的搬运工具。
///
/// 三个设计要点，都是吞吐的关键：
///
/// 1. 一次读满 64KB。按默认 4KB 读会变成每 4KB 一次「读 → 帧 → 内核写」，
///    端到端吞吐实测差一倍以上。
/// 2. 同一批内尽量多读：只要对端还有数据可读（<c>Socket.Available</c> /
///    <c>NetworkStream.DataAvailable</c>）就继续读，最多攒一批再写一次管道；
///    对端没数据时立刻写，交互型流量不会因此变慢。
/// 3. 缓冲归 pump 自己所有（ArrayPool 租还），不借用目标管道的内部缓冲——
///    连接关闭时 pump 可能还在写，借用别人的缓冲会有「数据被提前还池」的竞态。
///
/// 不用 <c>Stream.CopyToAsync</c>：它会从 ArrayPool 租 81920 字节中转缓冲，
/// 多一次全量 memcpy 和一份每连接的池压力。
/// </summary>
internal static class PipePumps
{
    /// <summary>单次读取的块大小：一次 socket 读正好填满。</summary>
    internal const int ChunkSize = 64 * 1024;

    /// <summary>
    /// socket → 管道。读到 EOF 或连接断开就返回，不主动 Complete 管道（由调用方统一收尾）。
    /// </summary>
    public static async Task SocketToPipeAsync(
        Socket socket, PipeWriter writer, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var total = 0;
                var eof = false;

                // 内核缓冲里还有数据就继续读，攒满这一块再写。
                // 注意读取长度必须被剩余空间夹住：ArrayPool 给的数组长度就是 ChunkSize，
                // 多读一次就会越界（曾经把整条转发连接打成 IO 异常）。
                while (total < buffer.Length)
                {
                    int read;
                    try
                    {
                        read = await socket
                            .ReceiveAsync(buffer.AsMemory(total, buffer.Length - total), SocketFlags.None, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (read == 0)
                    {
                        eof = true;
                        break;
                    }

                    total += read;

                    if (socket.Available == 0)
                    {
                        break;
                    }
                }

                if (total > 0 && !await WriteAsync(writer, buffer, total, cancellationToken))
                {
                    return;
                }

                if (eof)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 流 → 管道。<see cref="NetworkStream"/> 走「尽量读满一批」的快路径，
    /// 其它流（TLS 等拿不到可读字节数的）退化成一次读一写。
    /// </summary>
    public static async Task StreamToPipeAsync(
        Stream source, PipeWriter writer, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var networkStream = source as NetworkStream;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var total = 0;
                var eof = false;

                while (total < buffer.Length)
                {
                    int read;
                    try
                    {
                        read = await source
                            .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }

                    if (read == 0)
                    {
                        eof = true;
                        break;
                    }

                    total += read;

                    if (networkStream is null || !networkStream.DataAvailable)
                    {
                        break;
                    }
                }

                if (total > 0 && !await WriteAsync(writer, buffer, total, cancellationToken))
                {
                    return;
                }

                if (eof)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>写一段并 flush；目标管道已关闭时返回 false。</summary>
    private static async Task<bool> WriteAsync(
        PipeWriter writer, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        try
        {
            var flush = await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);

            return !flush.IsCompleted && !flush.IsCanceled;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // 管道已完成（流被对端关闭）
            return false;
        }
    }
}
