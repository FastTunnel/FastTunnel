// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Mux;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// 把一条 WebSocket 连接包装成 <see cref="IDuplexPipe"/>，供 <see cref="MuxSession"/> 使用。
///
/// 存在的意义是穿透能力：不少企业网络只放行 80/443，非 HTTP 的长连接一律被切断，
/// 而 WebSocket 是一次标准的 HTTP Upgrade —— 既能过这类防火墙，也能被 nginx / CDN
/// 按路径（/mux）反代到服务端。协议语义与 TCP 完全一致，MUX 帧字节流封装进二进制消息即可。
///
/// 与 <see cref="SocketDuplexPipe"/> 的差别：
/// - 接收侧多一个 pump 任务，把 WebSocket 消息写进 Pipe；
/// - 发送侧是自定义 PipeWriter，一次 Flush 对应一条二进制消息，
///   避免 MuxSession 的一帧被拆成多条消息（每拆一条就多一个 WS 头）。
/// </summary>
public sealed class WebSocketDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    /// <summary>单次接收缓冲。与 <see cref="MuxFrame.MaxPayload"/> 同量级，一帧数据一次读满。</summary>
    private const int ReceiveBufferSize = MuxFrame.MaxPayload;

    /// <summary>写缓冲超过该阈值后回收，避免偶发大帧把常驻内存抬高。</summary>
    private const int MaxRetainedBuffer = 64 * 1024;

    private readonly WebSocket socket;
    private readonly Pipe input;
    private readonly PipeWriter output;
    private readonly CancellationTokenSource cts = new();
    private readonly Task receiveLoop;

    private int disposed;

    public WebSocketDuplexPipe(WebSocket socket)
    {
        this.socket = socket;

        // pauseWriterThreshold 提供背压：上层读得慢时接收 pump 会停在 FlushAsync，
        // 不再从 socket 取数据，压力最终落到 TCP 窗口上。
        input = new Pipe(new PipeOptions(
            pauseWriterThreshold: ReceiveBufferSize * 4L,
            resumeWriterThreshold: ReceiveBufferSize * 2L,
            minimumSegmentSize: ReceiveBufferSize,
            useSynchronizationContext: false));

        output = new WebSocketPipeWriter(socket, MuxFrame.MaxPayload + MuxFrame.HeaderSize, MaxRetainedBuffer);

        receiveLoop = ReceiveLoopAsync(cts.Token);
    }

    public PipeReader Input => input.Reader;

    public PipeWriter Output => output;

    /// <summary>
    /// 接收 pump：把 WebSocket 二进制消息按到达顺序摊平成连续字节流。
    /// 消息边界对上层没有意义——MUX 自己按 9 字节帧头切分。
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var writer = input.Writer;
        Exception? error = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var memory = writer.GetMemory(ReceiveBufferSize);
                var result = await socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                writer.Advance(result.Count);

                var flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                    // 上层已不再读取，没必要继续收
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常释放
        }
        catch (WebSocketException ex)
        {
            error = ex;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            await writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await cts.CancelAsync();
        }
        catch
        {
        }

        // 先告知对端不再发送（失败也无所谓，连接可能已经断了）
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
        }
        catch
        {
        }

        try
        {
            await receiveLoop;
        }
        catch
        {
        }

        try
        {
            await output.CompleteAsync();
        }
        catch
        {
        }

        try
        {
            await input.Reader.CompleteAsync();
        }
        catch
        {
        }

        socket.Dispose();
        cts.Dispose();
    }

    /// <summary>
    /// 带缓冲的 <see cref="PipeWriter"/>：写入先落在自有数组里，<see cref="FlushAsync"/> 时
    /// 一次性作为一条 WebSocket 二进制消息发出。写操作由 <see cref="MuxSession"/> 的写锁串行化，
    /// 因此这里不需要再加锁。
    /// </summary>
    private sealed class WebSocketPipeWriter : PipeWriter
    {
        private readonly WebSocket socket;
        private readonly int initialSize;
        private readonly int maxRetained;

        private byte[] buffer;
        private int written;
        private bool completed;

        public WebSocketPipeWriter(WebSocket socket, int initialSize, int maxRetained)
        {
            this.socket = socket;
            this.initialSize = initialSize;
            this.maxRetained = maxRetained;
            buffer = new byte[initialSize];
        }

        public override void Advance(int bytes)
        {
            ObjectDisposedException.ThrowIf(completed, this);

            if (bytes < 0 || bytes > buffer.Length - written)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            written += bytes;
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return buffer.AsMemory(written);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return buffer.AsSpan(written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(completed, this);

            if (sizeHint < 1)
            {
                sizeHint = 1;
            }

            if (buffer.Length - written >= sizeHint)
            {
                return;
            }

            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, written + sizeHint));
        }

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (written == 0)
            {
                return new FlushResult(isCanceled: false, isCompleted: completed);
            }

            var count = written;
            written = 0;

            try
            {
                await socket.SendAsync(
                    buffer.AsMemory(0, count), WebSocketMessageType.Binary,
                    endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return new FlushResult(isCanceled: false, isCompleted: true);
            }
            catch (WebSocketException)
            {
                return new FlushResult(isCanceled: false, isCompleted: true);
            }

            // 大帧过后把缓冲收回去，避免常驻内存被一次峰值抬高
            if (buffer.Length > maxRetained)
            {
                buffer = new byte[initialSize];
            }

            return new FlushResult(isCanceled: false, isCompleted: false);
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            if (!completed)
            {
                completed = true;
            }

            return ValueTask.CompletedTask;
        }

        public override void Complete(Exception? exception = null)
        {
            completed = true;
        }

        /// <summary>
        /// 没有「挂起的 Flush」可控：发送是直接 await 在 SendAsync 上的，
        /// 取消由调用方传进来的 CancellationToken 负责。
        /// </summary>
        public override void CancelPendingFlush()
        {
        }
    }
}
