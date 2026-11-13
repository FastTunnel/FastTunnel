// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Mux;

/// <summary>
/// 多路复用上的一条逻辑流，对外就是一个 <see cref="IDuplexPipe"/>：
/// <list type="bullet">
/// <item><see cref="Input"/> 是接收侧，对端发来的字节从这里读；</item>
/// <item><see cref="Output"/> 是发送侧，写进去的字节会被后台任务切成 Data 帧发出。</item>
/// </list>
/// 两个方向各自独立，这样才能复用上层已有的 <c>PipeReader.CopyToAsync</c> 零拷贝搬运逻辑。
/// </summary>
public sealed class MuxStream : Stream, IDuplexPipe
{
    private readonly MuxSession _session;
    private readonly ILogger _logger;

    private readonly Pipe _receive;
    private readonly Pipe _send;
    private readonly MuxPipeReader _input;

    private readonly object _gate = new();

    /// <summary>发送窗口剩余字节数，由对端的 WindowUpdate 补充。</summary>
    private int _sendWindow = MuxFrame.InitialWindow;

    /// <summary>已消费但还没回补给对端的字节数。</summary>
    private int _consumedSinceUpdate;

    private TaskCompletionSource? _windowSignal;

    private Task? _sendLoopTask;
    private int _disposed;
    private int _aborted;
    private int _finSent;

    public uint Id { get; }

    public MuxStream(MuxSession session, uint id, ILogger logger)
    {
        _session = session;
        _logger = logger;
        Id = id;

        // 接收管道的暂停阈值必须 >= 窗口 + 单帧上限：
        // 发送方受窗口约束不会超发，因此 ReadLoop 里的 FlushAsync 永远不会被暂停，
        // 也就不会出现「一条慢流卡住整个会话读取」的队头阻塞。
        _receive = new Pipe(new PipeOptions(
            pauseWriterThreshold: MuxFrame.InitialWindow + MuxFrame.MaxPayload,
            resumeWriterThreshold: MuxFrame.InitialWindow,
            minimumSegmentSize: 4096,
            useSynchronizationContext: false));

        _send = new Pipe(new PipeOptions(useSynchronizationContext: false));
        _input = new MuxPipeReader(_receive.Reader, OnReceiveConsumed);
    }

    public PipeReader Input => _input;

    public PipeWriter Output => _send.Writer;

    // ---------------- Stream 适配 ----------------
    // 一条流等价于一个双向字节管道，因此也实现 Stream：
    // UDP 会话、旧式 Stream 消费者可以直接用，不必再包一层转换。

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _send.Writer.FlushAsync(cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            var result = await _input.ReadAsync(cancellationToken);
            var sequence = result.Buffer;

            if (!sequence.IsEmpty)
            {
                var count = (int)Math.Min(sequence.Length, buffer.Length);
                sequence.Slice(0, count).CopyTo(buffer.Span);

                // 必须按实际拷走的字节数推进，否则下次又会读到同一段数据
                _input.AdvanceTo(sequence.GetPosition(count));
                return count;
            }

            _input.AdvanceTo(sequence.Start, sequence.End);

            if (result.IsCompleted)
            {
                return 0;
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => await _send.Writer.WriteAsync(buffer, cancellationToken);

    /// <summary>接收侧的写入端，仅供 <see cref="MuxSession"/> 的读取循环使用。</summary>
    internal PipeWriter ReceiveWriter => _receive.Writer;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void Start()
    {
        _sendLoopTask ??= SendLoopAsync();
    }

    /// <summary>
    /// 把 <see cref="Output"/> 中的数据切成帧发出去。每个方向只有一个写入者
    /// （上层的单向泵），所以这里不需要额外的串行化。
    /// </summary>
    private async Task SendLoopAsync()
    {
        var reader = _send.Reader;
        var token = _session.Closed;

        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var buffer = result.Buffer;
                var consumed = buffer.Start;

                try
                {
                    while (buffer.Length > 0)
                    {
                        var chunk = buffer.Slice(0, (int)Math.Min(buffer.Length, MuxFrame.MaxPayload));
                        await _session.WriteDataAsync(this, chunk, token);
                        consumed = chunk.End;
                        buffer = buffer.Slice(chunk.End);
                    }
                }
                finally
                {
                    reader.AdvanceTo(consumed, result.Buffer.End);
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]流 {StreamId} 发送循环结束", Id);
        }
        finally
        {
            reader.Complete();

            // 本方向没有更多数据了，显式告诉对端，避免它一直等 EOF。
            if (Interlocked.Exchange(ref _finSent, 1) == 0)
            {
                try
                {
                    await _session.WriteFrameAsync(MuxFrameType.Fin, Id, ReadOnlyMemory<byte>.Empty, token);
                }
                catch
                {
                    // 会话已关闭
                }
            }
        }
    }

    /// <summary>对端发来 Fin：本流不再有新数据，让读取侧看到 EOF。</summary>
    internal void OnRemoteFin()
    {
        try
        {
            _receive.Writer.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]流 {StreamId} 完成接收侧失败", Id);
        }
    }

    /// <summary>对端异常中止。</summary>
    internal void OnRemoteRst()
    {
        OnRemoteFin();
        Dispose();
    }

    /// <summary>收到对端的窗口更新。</summary>
    internal void AddSendWindow(int delta)
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            _sendWindow += delta;
            signal = _windowSignal;
            _windowSignal = null;
        }

        signal?.TrySetResult();
    }

    /// <summary>
    /// 申请发送额度。窗口不足时挂起，直到对端回补或会话结束。
    /// 这是发送侧唯一的背压点：窗口用尽 = 对端消费不过来，就地等待，不做无界缓冲。
    /// </summary>
    internal async ValueTask WaitSendWindowAsync(int length, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                // 只有「中止」才拒绝发送；优雅关闭期间发送循环还要把队列里的数据补发完
                if (Volatile.Read(ref _aborted) != 0)
                {
                    throw new ObjectDisposedException($"MuxStream {Id}");
                }

                if (_sendWindow >= length)
                {
                    _sendWindow -= length;
                    return;
                }

                wait = (_windowSignal ??=
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>上层消费了 bytes 字节，按阈值回补窗口。</summary>
    internal void OnReceiveConsumed(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        int delta;
        lock (_gate)
        {
            _consumedSinceUpdate += bytes;
            if (_consumedSinceUpdate < MuxFrame.InitialWindow / 4)
            {
                return;
            }

            delta = _consumedSinceUpdate;
            _consumedSinceUpdate = 0;
        }

        _session.TrySendWindowUpdate(Id, delta);
    }

    /// <summary>
    /// 立即销毁：用于回调链里（如 RST 处理）或对端已不可用的场景，不保证已排队的数据发出去。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);

        if (disposing)
        {
            Cleanup();
        }
    }

    /// <summary>
    /// 优雅销毁：先完成发送侧，等发送循环把队列里剩下的数据真正写成帧（并发出 Fin），再回收资源。
    /// 如果直接 Dispose，最后一批数据会随缓冲区一起丢掉——HTTP 响应体正是这么被截断的。
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _send.Writer.Complete();
        }
        catch
        {
            // 已被完成
        }

        if (_sendLoopTask is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 超时或发送失败：下行已经没有意义，直接回收
            }
        }

        Cleanup();
    }

    private void Cleanup()
    {
        // 到这里不再接受新的发送（等待窗口的发送者也会被唤醒并退出）
        Volatile.Write(ref _aborted, 1);

        _session.RemoveStream(Id);

        try
        {
            _send.Writer.Complete();
        }
        catch
        {
            // 已被发送循环完成
        }

        OnRemoteFin();
        _input.Complete();

        // 唤醒所有等待窗口的发送者，让它们以 ObjectDisposedException 退出
        TaskCompletionSource? signal;
        lock (_gate)
        {
            signal = _windowSignal;
            _windowSignal = null;
        }

        signal?.TrySetResult();
    }
}

/// <summary>
/// 包一层 <see cref="PipeReader"/>，在 <c>AdvanceTo</c> 时统计被消费的字节数，
/// 用于驱动接收窗口回补。PipeReader 本身不提供「消费了多少」的通知，
/// 只能靠比较 SequencePosition 的偏移量来算。
/// </summary>
internal sealed class MuxPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly Action<int> _onConsumed;
    private ReadOnlySequence<byte> _lastBuffer;

    public MuxPipeReader(PipeReader inner, Action<int> onConsumed)
    {
        _inner = inner;
        _onConsumed = onConsumed;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        var delta = CalculateConsumed(consumed);
        _inner.AdvanceTo(consumed, examined);

        if (delta > 0)
        {
            _onConsumed(delta);
        }
    }

    private int CalculateConsumed(SequencePosition position)
    {
        if (_lastBuffer.IsEmpty)
        {
            return 0;
        }

        try
        {
            var offset = _lastBuffer.GetOffset(position) - _lastBuffer.GetOffset(_lastBuffer.Start);
            return offset <= 0 ? 0 : (int)offset;
        }
        catch (ArgumentOutOfRangeException)
        {
            // position 不属于当前 buffer（已被替换），保守按 0 处理，只影响窗口回补速度。
            return 0;
        }
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override bool TryRead(out ReadResult result)
    {
        if (_inner.TryRead(out result))
        {
            _lastBuffer = result.Buffer;
            return true;
        }

        return false;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        => ReadCoreAsync(cancellationToken);

    private async ValueTask<ReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var result = await _inner.ReadAsync(cancellationToken);
        _lastBuffer = result.Buffer;
        return result;
    }
}
