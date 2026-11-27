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
using FastTunnel.Core.Forwarder.Streams;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Mux;

/// <summary>
/// 多路复用上的一条逻辑流，对外就是一个 <see cref="IDuplexPipe"/>：
/// <list type="bullet">
/// <item><see cref="Input"/> 是接收侧，对端发来的字节从这里读；</item>
/// <item><see cref="Output"/> 是发送侧，写进去的字节立刻按帧发往对端。</item>
/// </list>
/// 两个方向各自独立，这样才能复用上层已有的 <c>PipeReader.CopyToAsync</c> 零拷贝搬运逻辑。
///
/// 发送侧没有中间管道：写入直接变成会话传输管道里的帧（一次拷贝），
/// 而不是「先拷进每流的发送管道 → 后台任务再拷进传输管道」（两次拷贝 + 一次异步跳转）。
/// 反压由窗口额度提供：窗口用尽时写者就地等待，不做无界缓冲。
/// </summary>
public sealed class MuxStream : Stream, IDuplexPipe, IDatagramWriter
{
    /// <summary>
    /// 接收侧消费到这么多字节就回补一次窗口。
    ///
    /// 取固定值而不是「窗口的一半」，是为了兼容旧对端：旧客户端的初始信用只有
    /// <see cref="MuxFrame.LegacyWindow"/>，回补阈值一旦超过它，旧对端会在发满初始信用后
    /// 永远等不到窗口更新而死锁。
    /// </summary>
    internal const int WindowUpdateThreshold = 128 * 1024;

    private readonly MuxSession _session;
    private readonly ILogger _logger;

    private readonly Pipe _receive;
    private readonly MuxPipeReader _input;
    private readonly MuxStreamWriter _output;

    private readonly object _gate = new();

    /// <summary>发送窗口剩余字节数，由对端的 WindowUpdate 补充。</summary>
    private int _sendWindow = MuxFrame.InitialWindow;

    /// <summary>已消费但还没回补给对端的字节数。</summary>
    private int _consumedSinceUpdate;

    /// <summary>是否已经有一个在途的窗口回补任务（同一时刻最多一个，避免拥塞时无限堆积）。</summary>
    private int _windowUpdateScheduled;

    private TaskCompletionSource? _windowSignal;

    private int _disposed;
    private int _aborted;
    private int _finSent;

    public uint Id { get; }
    internal CancellationToken ConnectionClosed => _session.Closed;

    public MuxStream(MuxSession session, uint id, ILogger logger)
    {
        _session = session;
        _logger = logger;
        Id = id;

        // 接收管道的暂停阈值 = 窗口 + 单帧上限：发送方受窗口约束不会超发，
        // 因此会话读取循环里的 WriteAsync/FlushAsync 永远不会被暂停，
        // 也就不会出现「一条慢流卡住整个会话读取」的队头阻塞。
        //
        // 段大小取 64KB：一个段能装下 4 个 16KB 帧，下游消费者（CopyToAsync）拿到的
        // 是 64KB 连续内存，写用户 socket 时一次内核写就能写完，而不是每帧写一次。
        _receive = new Pipe(new PipeOptions(
            pauseWriterThreshold: MuxFrame.InitialWindow + MuxFrame.MaxPayload,
            resumeWriterThreshold: MuxFrame.InitialWindow,
            minimumSegmentSize: 64 * 1024,
            useSynchronizationContext: false));

        _input = new MuxPipeReader(_receive.Reader, OnReceiveConsumed);
        _output = new MuxStreamWriter(this);
    }

    public PipeReader Input => _input;

    public PipeWriter Output => _output;

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
        => _output.FlushAsync(cancellationToken).AsTask();

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
        => await _output.WriteAsync(buffer, cancellationToken);

    /// <summary>
    /// UDP 数据报专用写：把 [2 字节长度][负载] 作为一个整体交给会话层，
    /// 在写锁内和 MUX 帧头一起写进传输缓冲。相比「先拼帧再写流」少一次全量拷贝，
    /// 且无论数据报多大都只 flush 一次。
    /// </summary>
    public async ValueTask WriteDatagramAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > UdpDatagramFramer.MaxDatagramSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "UDP 数据报超过最大长度");
        }

        // 长度前缀和数据报必须连续，因此一次性预留整条的窗口额度。
        await ReserveSendWindowAsync(2 + payload.Length, cancellationToken).ConfigureAwait(false);
        await _session.WriteDatagramAsync(this, payload, cancellationToken).ConfigureAwait(false);
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>测试用：当前剩余发送额度（字节）。</summary>
    internal int SendWindow => Volatile.Read(ref _sendWindow);

    /// <summary>
    /// 把对端发来的一帧数据交给上层消费者。返回 true 表示上层已经不需要这条流了。
    /// 负载是会话读取缓冲里的切片，这里只做一次「会话管道 → 流管道」的搬运。
    /// </summary>
    internal async ValueTask<bool> ReceiveAsync(ReadOnlySequence<byte> payload)
    {
        var writer = _receive.Writer;

        foreach (var segment in payload)
        {
            writer.Write(segment.Span);
        }

        var flush = await writer.FlushAsync(_session.Closed);
        return flush.IsCompleted;
    }

    /// <summary>
    /// 把一段数据按窗口额度切成若干 Data 帧发出去。整段只取一次写锁、只 flush 一次。
    /// </summary>
    internal async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        while (!payload.IsEmpty)
        {
            var want = Math.Min(payload.Length, MuxSession.MaxWriteBatch);
            var granted = await WaitSendWindowAsync(want, cancellationToken);

            var chunk = payload.Slice(0, granted);
            await _session.WriteDataAsync(this, chunk, cancellationToken);
            payload = payload.Slice(granted);
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
    /// 申请发送额度，返回本次获批的字节数（1 ≤ granted ≤ want）。
    /// 窗口为 0 时挂起，直到对端回补或会话结束。
    /// </summary>
    internal async ValueTask<int> WaitSendWindowAsync(int want, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (Volatile.Read(ref _aborted) != 0)
                {
                    throw new ObjectDisposedException($"MuxStream {Id}");
                }

                if (_sendWindow > 0)
                {
                    var granted = Math.Min(_sendWindow, want);
                    _sendWindow -= granted;
                    return granted;
                }

                wait = (_windowSignal ??=
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 预留恰好 want 字节的发送额度。UDP 数据报的长度前缀与负载必须连续，
    /// 不能像普通字节流那样按剩余窗口截断，所以这里等整条额度到齐再返回。
    /// </summary>
    private async ValueTask ReserveSendWindowAsync(int want, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (Volatile.Read(ref _aborted) != 0)
                {
                    throw new ObjectDisposedException($"MuxStream {Id}");
                }

                if (_sendWindow >= want)
                {
                    _sendWindow -= want;
                    return;
                }

                wait = (_windowSignal ??=
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 上层消费了 bytes 字节：累计到阈值后回补窗口。
    /// 这条路径在每次 AdvanceTo 时都会走，必须无锁无分配，所以只用 Interlocked。
    /// </summary>
    internal void OnReceiveConsumed(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (Interlocked.Add(ref _consumedSinceUpdate, bytes) < WindowUpdateThreshold)
        {
            return;
        }

        ScheduleWindowUpdate();
    }

    private void ScheduleWindowUpdate()
    {
        // 同一时刻只允许一个在途回补：链路拥塞时不会堆出一串待发任务，
        // 这期间的新增量会在下一轮被一起带走。
        if (Interlocked.CompareExchange(ref _windowUpdateScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = SendWindowUpdatesAsync();
    }

    private async Task SendWindowUpdatesAsync()
    {
        try
        {
            while (true)
            {
                var delta = Interlocked.Exchange(ref _consumedSinceUpdate, 0);
                if (delta <= 0)
                {
                    break;
                }

                await _session.SendWindowUpdateAsync(Id, delta);
            }
        }
        catch
        {
            // 会话关闭：窗口回补已无意义
        }
        finally
        {
            Volatile.Write(ref _windowUpdateScheduled, 0);

            // 清标记与「又有新消费」可能并发：再检查一次，避免最后一次回补永久丢失
            if (Volatile.Read(ref _consumedSinceUpdate) >= WindowUpdateThreshold)
            {
                ScheduleWindowUpdate();
            }
        }
    }

    /// <summary>本方向数据发完：发出 Fin（半关闭）。重复调用只发一次。</summary>
    internal async ValueTask CompleteOutputAsync()
    {
        _output.MarkCompleted();

        if (Interlocked.Exchange(ref _finSent, 1) != 0)
        {
            return;
        }

        try
        {
            await _session.WriteFrameAsync(MuxFrameType.Fin, Id, ReadOnlyMemory<byte>.Empty, _session.Closed);
        }
        catch
        {
            // 会话已关闭
        }
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
    /// 优雅销毁：先发掉 Fin，再回收资源。
    /// 发送是「写调用同步落进传输管道」的，没有需要排空的队列，
    /// 所以这里只需要在写锁后面追一个 Fin，就能保证对端读完数据后再看到 EOF。
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CompleteOutputAsync();
        Cleanup();
    }

    private void Cleanup()
    {
        // 到这里不再接受新的发送（等待窗口的发送者也会被唤醒并退出）
        Volatile.Write(ref _aborted, 1);

        _session.RemoveStream(Id);
        _output.ReleaseBuffer();
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

    /// <summary>
    /// 流的发送侧。两种写入方式都直接落到会话的传输管道里：
    /// <list type="bullet">
    /// <item><c>WriteAsync</c>：零中转，按窗口额度切帧后一次 flush；</item>
    /// <item><c>GetMemory/Advance/FlushAsync</c>：先写进一块池化缓冲，Flush 时再发出去
    ///       （少数按「取内存自行填充」方式写管道的调用方需要它）。</item>
    /// </list>
    /// </summary>
    internal sealed class MuxStreamWriter : PipeWriter, IDirectBufferPipeWriter
    {
        private const int InitialBufferSize = 64 * 1024;

        private readonly MuxStream _stream;
        private byte[]? _buffer;
        private int _written;
        private bool _completed;

        public MuxStreamWriter(MuxStream stream) => _stream = stream;

        public override void Advance(int bytes)
        {
            if (bytes < 0 || _buffer is null || _written + bytes > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            _written += bytes;
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_written == 0)
            {
                return new FlushResult(isCanceled: false, isCompleted: _completed);
            }

            var count = _written;
            _written = 0;

            try
            {
                await _stream.SendAsync(_buffer.AsMemory(0, count), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                return new FlushResult(isCanceled: false, isCompleted: true);
            }
            catch (OperationCanceledException)
            {
                return new FlushResult(isCanceled: true, isCompleted: false);
            }

            return new FlushResult(isCanceled: false, isCompleted: _completed);
        }

        public override async ValueTask<FlushResult> WriteAsync(
            ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            if (_written > 0)
            {
                await FlushAsync(cancellationToken);
            }

            if (source.IsEmpty)
            {
                return new FlushResult(isCanceled: false, isCompleted: _completed);
            }

            try
            {
                await _stream.SendAsync(source, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                return new FlushResult(isCanceled: false, isCompleted: true);
            }
            catch (OperationCanceledException)
            {
                return new FlushResult(isCanceled: true, isCompleted: false);
            }

            return new FlushResult(isCanceled: false, isCompleted: _completed);
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
            => _stream.CompleteOutputAsync();

        public override void Complete(Exception? exception = null)
        {
            // 同步路径不能等 Fin 写完，交给后台收尾
            _ = _stream.CompleteOutputAsync().AsTask();
        }

        /// <summary>没有挂起的 flush 可控：发送直接 await 在 SendAsync 上，取消由调用方的 token 负责。</summary>
        public override void CancelPendingFlush()
        {
        }

        public void MarkCompleted() => _completed = true;

        public void ReleaseBuffer()
        {
            // 只断开引用，不归还共享池：连接关闭时可能还有一次写在途，
            // 把数组还池会让别人复用到正在发送的数据。
            // 该缓冲只在调用方按 GetMemory/Advance 方式写管道时才会分配（数据面走 WriteAsync，不会用到）。
            Interlocked.Exchange(ref _buffer, null);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (_completed)
            {
                throw new InvalidOperationException("流已关闭");
            }

            if (sizeHint < 1)
            {
                sizeHint = 1;
            }

            if (_buffer != null && _buffer.Length - _written >= sizeHint)
            {
                return;
            }

            var needed = Math.Max(InitialBufferSize, _written + sizeHint);
            var buffer = ArrayPool<byte>.Shared.Rent(needed);

            if (_buffer != null)
            {
                if (_written > 0)
                {
                    Array.Copy(_buffer, buffer, _written);
                }

                ArrayPool<byte>.Shared.Return(_buffer);
            }

            _buffer = buffer;
        }
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
