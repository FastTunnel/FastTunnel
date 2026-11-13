// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Mux;

/// <summary>
/// 多路复用会话：一条 TCP 连接上承载任意多条逻辑流。
///
/// 与旧实现的关键差别：以前每来一条公网连接，服务端就要通知客户端回连一条新 TCP（PROXY 伪请求），
/// 一次连接固定付 1~2 个 RTT 的握手成本；有了会话，新连接只是本地分配一个 streamId 并写一个 SYN 帧，
/// 零握手。这是把「每连接一条 TCP」变成「每连接一条流」的核心。
///
/// streamId 分配：服务端用奇数，客户端用偶数，各自单调递增，单连接内互不冲突。
/// </summary>
public sealed class MuxSession : IAsyncDisposable
{
    private readonly IDuplexPipe _transport;
    private readonly ILogger _logger;
    private readonly bool _isServer;
    private readonly Func<MuxStream, SwapRequest, Task>? _onAccept;

    private readonly ConcurrentDictionary<uint, MuxStream> _streams = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private int _nextStreamId;
    private int _closed;

    public MuxSession(
        IDuplexPipe transport,
        bool isServer,
        ILogger logger,
        Func<MuxStream, SwapRequest, Task>? onAccept = null)
    {
        _transport = transport;
        _isServer = isServer;
        _logger = logger;
        _onAccept = onAccept;
        _nextStreamId = isServer ? 1 : 2;
    }

    /// <summary>会话关闭token，所有流、所有泵都挂着它。</summary>
    public CancellationToken Closed => _cts.Token;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public int StreamCount => _streams.Count;

    /// <summary>
    /// 服务端发起一条新流，SYN 里携带内网目标地址，客户端收到后直连该地址。
    /// </summary>
    public async Task<MuxStream> OpenStreamAsync(SwapRequest target, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            throw new IOException("多路复用会话已关闭");
        }

        if (_streams.Count >= MuxFrame.MaxStreamCount)
        {
            throw new InvalidOperationException($"多路复用流数已达上限 {MuxFrame.MaxStreamCount}");
        }

        var id = AllocateStreamId();
        var stream = new MuxStream(this, id, _logger);
        if (!_streams.TryAdd(id, stream))
        {
            throw new InvalidOperationException($"streamId 冲突 {id}");
        }

        stream.Start();

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(target, ControlProtocol.JsonOptions);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Closed);
            await WriteFrameAsync(MuxFrameType.Syn, id, payload, linked.Token);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return stream;
    }

    /// <summary>
    /// 会话主循环：解析帧并分派。整个会话只有这一个读取者，
    /// 因此帧的到达顺序与流内顺序天然一致。
    /// </summary>
    public async Task RunAsync()
    {
        var input = _transport.Input;
        var header = new byte[MuxFrame.HeaderSize];

        try
        {
            while (!IsClosed)
            {
                await ReadExactAsync(input, header, Closed);

                var (type, streamId, length) = MuxFrame.ReadHeader(header);
                await HandleFrameAsync(input, type, streamId, length);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (EndOfStreamException)
        {
            // 对端断开
        }
        catch (IOException ex)
        {
            // 客户端掉线、RST 都是这种情况，属于正常路径，不必打完整堆栈
            _logger.LogDebug(ex, "[Mux]连接已断开");
        }
        catch (ObjectDisposedException)
        {
            // 会话已被释放
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mux]会话异常结束");
        }
        finally
        {
            await CloseAsync();
        }
    }

    private async Task HandleFrameAsync(PipeReader input, MuxFrameType type, uint streamId, int length)
    {
        switch (type)
        {
            case MuxFrameType.Syn:
                await HandleSynAsync(input, streamId, length);
                break;

            case MuxFrameType.Data:
                await HandleDataAsync(input, streamId, length);
                break;

            case MuxFrameType.Fin:
                _streams.TryGetValue(streamId, out var finStream);
                finStream?.OnRemoteFin();
                break;

            case MuxFrameType.Rst:
                if (_streams.TryRemove(streamId, out var rstStream))
                {
                    rstStream.OnRemoteRst();
                }

                break;

            case MuxFrameType.WindowUpdate:
                await HandleWindowUpdateAsync(input, streamId, length);
                break;

            case MuxFrameType.Ping:
                await SkipPayloadAsync(input, length, Closed);
                await WriteFrameAsync(MuxFrameType.Pong, 0, ReadOnlyMemory<byte>.Empty, Closed);
                break;

            case MuxFrameType.Pong:
                await SkipPayloadAsync(input, length, Closed);
                break;

            case MuxFrameType.GoAway:
                throw new EndOfStreamException("对端关闭多路复用会话");

            default:
                throw new InvalidOperationException($"未知帧类型 {type}");
        }
    }

    private async Task HandleSynAsync(PipeReader input, uint streamId, int length)
    {
        if (_isServer)
        {
            // 服务端不接收客户端发起的流（stcp / xtcp 才会用到，暂不支持）
            await SkipPayloadAsync(input, length, Closed);
            await WriteFrameAsync(MuxFrameType.Rst, streamId, ReadOnlyMemory<byte>.Empty, Closed);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            await ReadExactAsync(input, buffer.AsMemory(0, length), Closed);

            SwapRequest? target;
            try
            {
                target = JsonSerializer.Deserialize<SwapRequest>(
                    buffer.AsSpan(0, length), ControlProtocol.JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[Mux]SYN 载荷解析失败 stream={StreamId}", streamId);
                target = null;
            }

            if (target == null || string.IsNullOrEmpty(target.Host) || target.Port <= 0)
            {
                await WriteFrameAsync(MuxFrameType.Rst, streamId, ReadOnlyMemory<byte>.Empty, Closed);
                return;
            }

            var stream = new MuxStream(this, streamId, _logger);
            if (!_streams.TryAdd(streamId, stream))
            {
                // id 已被占用：客户端用了服务端的号段，属于协议错误
                _logger.LogWarning("[Mux]streamId 冲突 {StreamId}", streamId);
                await WriteFrameAsync(MuxFrameType.Rst, streamId, ReadOnlyMemory<byte>.Empty, Closed);
                return;
            }

            stream.Start();

            if (_onAccept != null)
            {
                // 交给业务层去连本地目标，不能阻塞读取循环
                _ = AcceptAsync(stream, target);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private uint AllocateStreamId()
    {
        // 服务端起于 1、客户端起于 2，步长 2，两条号段互不重叠
        var id = Interlocked.Add(ref _nextStreamId, 2) - 2;
        return (uint)id;
    }

    private async Task AcceptAsync(MuxStream stream, SwapRequest target)
    {
        try
        {
            await _onAccept!(stream, target);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]处理流 {StreamId} 失败", stream.Id);
            stream.Dispose();
        }
    }

    private async Task HandleDataAsync(PipeReader input, uint streamId, int length)
    {
        if (length == 0)
        {
            return;
        }

        if (!_streams.TryGetValue(streamId, out var stream) || stream.IsDisposed)
        {
            // 流已关闭，数据丢弃，但必须把帧体读掉，否则会话字节流错位
            await SkipPayloadAsync(input, length, Closed);
            return;
        }

        Memory<byte> memory;
        try
        {
            memory = stream.ReceiveWriter.GetMemory(length);
        }
        catch (Exception)
        {
            _streams.TryRemove(streamId, out _);
            stream.Dispose();
            await SkipPayloadAsync(input, length, Closed);
            return;
        }

        await ReadExactAsync(input, memory.Slice(0, length), Closed);
        stream.ReceiveWriter.Advance(length);

        var flush = await stream.ReceiveWriter.FlushAsync(Closed);
        if (flush.IsCompleted)
        {
            // 上层已经不再读取，这条流没有意义了
            _streams.TryRemove(streamId, out _);
            stream.Dispose();
        }
    }

    private async Task HandleWindowUpdateAsync(PipeReader input, uint streamId, int length)
    {
        if (length != 4)
        {
            await SkipPayloadAsync(input, length, Closed);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            await ReadExactAsync(input, buffer.AsMemory(0, 4), Closed);
            var delta = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buffer);
            if (delta > 0 && _streams.TryGetValue(streamId, out var stream))
            {
                stream.AddSendWindow(delta);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------- 帧写入 ----------------

    internal async ValueTask WriteDataAsync(MuxStream stream, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        await stream.WaitSendWindowAsync((int)payload.Length, cancellationToken);
        await WriteFrameAsync(MuxFrameType.Data, stream.Id, payload, cancellationToken);
    }

    internal async ValueTask WriteFrameAsync(
        MuxFrameType type, uint streamId, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var output = _transport.Output;

            var header = output.GetSpan(MuxFrame.HeaderSize);
            MuxFrame.WriteHeader(header, type, streamId, (int)payload.Length);
            output.Advance(MuxFrame.HeaderSize);

            foreach (var segment in payload)
            {
                output.Write(segment.Span);
            }

            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal ValueTask WriteFrameAsync(
        MuxFrameType type, uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.IsEmpty)
        {
            return WriteFrameAsync(type, streamId, ReadOnlySequence<byte>.Empty, cancellationToken);
        }

        return WriteFrameAsync(type, streamId, new ReadOnlySequence<byte>(payload), cancellationToken);
    }

    /// <summary>回调链里的尽力而为发送：失败即丢弃，绝不抛出到业务代码。</summary>
    internal void TrySendFrame(MuxFrameType type, uint streamId, ReadOnlyMemory<byte> payload)
    {
        if (IsClosed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WriteFrameAsync(type, streamId, payload, Closed);
            }
            catch
            {
                // 会话已关闭或连接已断，无需处理
            }
        });
    }

    internal void TrySendWindowUpdate(uint streamId, int delta)
    {
        var buffer = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, delta);
        TrySendFrame(MuxFrameType.WindowUpdate, streamId, buffer);
    }

    // ---------------- 关闭 ----------------

    internal void RemoveStream(uint streamId) => _streams.TryRemove(streamId, out _);

    /// <summary>链路保活：对端会回 Pong，同时让 NAT 保持映射。</summary>
    public ValueTask SendPingAsync(CancellationToken cancellationToken)
        => WriteFrameAsync(MuxFrameType.Ping, 0, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        foreach (var kv in _streams)
        {
            try
            {
                kv.Value?.Dispose();
            }
            catch
            {
            }
        }

        _streams.Clear();

        try
        {
            // 尽力通知对端，写不出去也无所谓
            await WriteFrameAsync(MuxFrameType.GoAway, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    // ---------------- 读取工具 ----------------

    private static ValueTask SkipPayloadAsync(PipeReader input, int length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return ValueTask.CompletedTask;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        return SkipCoreAsync(input, buffer, length, cancellationToken);

        static async ValueTask SkipCoreAsync(PipeReader reader, byte[] rented, int count, CancellationToken token)
        {
            try
            {
                await ReadExactAsync(reader, rented.AsMemory(0, count), token);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>从 PipeReader 精确读满指定长度。数据不足时挂起等待，EOF 抛 <see cref="EndOfStreamException"/>。</summary>
    internal static async ValueTask ReadExactAsync(PipeReader reader, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < destination.Length)
        {
            var result = await reader.ReadAsync(cancellationToken);

            if (result.IsCanceled)
            {
                throw new OperationCanceledException();
            }

            var buffer = result.Buffer;
            var count = (int)Math.Min(buffer.Length, destination.Length - filled);

            if (count == 0)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    throw new EndOfStreamException();
                }

                continue;
            }

            buffer.Slice(0, count).CopyTo(destination.Span.Slice(filled));
            reader.AdvanceTo(buffer.GetPosition(count));
            filled += count;

            if (filled < destination.Length && result.IsCompleted)
            {
                throw new EndOfStreamException();
            }
        }
    }
}
