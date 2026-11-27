// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;
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
/// 吞吐相关的三条规则（改这个文件前先读）：
/// 1. 读取侧是「一次 ReadAsync 尽量多解帧」的循环，不再一帧一次 ReadAsync/AdvanceTo；
///    一帧的负载直接以切片（ReadOnlySequence）交给流，不租 ArrayPool 中转。
/// 2. 写入侧按批走：一条流的连续数据在同一个写锁里切成多帧、只 flush 一次，
///    因此每批最多一次内核写，而不是每帧一次。
/// 3. 控制帧（窗口回补）绝不 Task.Run、绝不为 4 字节负载分配数组，也不阻塞读取循环。
///
/// streamId 分配：服务端用奇数，客户端用偶数，各自单调递增，单连接内互不冲突。
/// </summary>
public sealed class MuxSession : IAsyncDisposable
{
    /// <summary>
    /// 一次写锁内最多搬运多少字节。越大 flush 次数越少，越小多流交织越公平；
    /// 256KB 在 1Gbps 链路上约 2ms，既能把几十帧合成一次写，又不至于让单条流独占链路。
    /// </summary>
    internal const int MaxWriteBatch = 256 * 1024;

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

        try
        {
            while (!IsClosed)
            {
                var result = await input.ReadAsync(Closed);

                if (result.IsCanceled)
                {
                    break;
                }

                // 一次拿到多少就解多少帧：每帧不再单独 ReadAsync + AdvanceTo，
                // 只有「数据不够、需要等对端」时才回到 ReadAsync。
                var buffer = result.Buffer;
                var consumed = buffer.Start;

                try
                {
                    while (TryReadFrame(buffer, out var type, out var streamId, out var payload, out var total))
                    {
                        buffer = buffer.Slice(total);
                        consumed = buffer.Start;
                        await HandleFrameAsync(type, streamId, payload);
                    }
                }
                finally
                {
                    input.AdvanceTo(consumed, buffer.End);
                }

                if (result.IsCompleted)
                {
                    // 对端已关闭，缓冲里剩余的半个帧没有意义
                    break;
                }
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

    /// <summary>
    /// 从会话缓冲里切出一个完整帧。缓冲不足一帧时返回 false，由调用方等待更多数据。
    /// 帧头用 <see cref="SequenceReader{T}"/> 解析，跨段也不会多一次拷贝。
    /// </summary>
    private static bool TryReadFrame(
        ReadOnlySequence<byte> buffer,
        out MuxFrameType type,
        out uint streamId,
        out ReadOnlySequence<byte> payload,
        out long totalLength)
    {
        type = default;
        streamId = 0;
        payload = default;
        totalLength = 0;

        if (buffer.Length < MuxFrame.HeaderSize)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryRead(out var typeByte)
            || !reader.TryReadBigEndian(out int id)
            || !reader.TryReadBigEndian(out int length))
        {
            return false;
        }

        // 长度字段来自对端，必须校验，否则一个坏帧就能让进程 OOM
        if (length < 0 || length > MuxFrame.MaxPayload)
        {
            throw new InvalidOperationException($"非法帧长度 {length}");
        }

        if (buffer.Length - MuxFrame.HeaderSize < length)
        {
            return false;
        }

        type = (MuxFrameType)typeByte;
        streamId = (uint)id;
        payload = buffer.Slice(MuxFrame.HeaderSize, length);
        totalLength = MuxFrame.HeaderSize + length;
        return true;
    }

    private async ValueTask HandleFrameAsync(
        MuxFrameType type, uint streamId, ReadOnlySequence<byte> payload)
    {
        switch (type)
        {
            case MuxFrameType.Data:
                await HandleDataAsync(streamId, payload);
                break;

            case MuxFrameType.Syn:
                await HandleSynAsync(streamId, payload);
                break;

            case MuxFrameType.Fin:
                if (_streams.TryGetValue(streamId, out var finStream))
                {
                    finStream.OnRemoteFin();
                }

                break;

            case MuxFrameType.Rst:
                if (_streams.TryRemove(streamId, out var rstStream))
                {
                    rstStream.OnRemoteRst();
                }

                break;

            case MuxFrameType.WindowUpdate:
                // 注意不能直接用 payload.FirstSpan：4 字节负载也可能跨段，
                // 那样读 4 字节会抛 ArgumentOutOfRange，把整条会话带走。
                if (payload.Length == 4 && _streams.TryGetValue(streamId, out var windowStream))
                {
                    Span<byte> delta = stackalloc byte[4];
                    payload.CopyTo(delta);
                    windowStream.AddSendWindow(BinaryPrimitives.ReadInt32BigEndian(delta));
                }

                break;

            case MuxFrameType.Ping:
                await WriteFrameAsync(MuxFrameType.Pong, 0, ReadOnlyMemory<byte>.Empty, Closed);
                break;

            case MuxFrameType.Pong:
                break;

            case MuxFrameType.GoAway:
                throw new EndOfStreamException("对端关闭多路复用会话");

            default:
                throw new InvalidOperationException($"未知帧类型 {type}");
        }
    }

    private async ValueTask HandleSynAsync(uint streamId, ReadOnlySequence<byte> payload)
    {
        if (_isServer)
        {
            // 服务端不接收客户端发起的流（stcp / xtcp 才会用到，暂不支持）
            await WriteFrameAsync(MuxFrameType.Rst, streamId, ReadOnlyMemory<byte>.Empty, Closed);
            return;
        }

        SwapRequest? target;
        byte[]? rented = null;
        try
        {
            // SYN 载荷很小且通常连续；跨段时租一次数组兜底，不在热路径上
            ReadOnlySpan<byte> json;
            if (payload.IsSingleSegment)
            {
                json = payload.FirstSpan;
            }
            else
            {
                rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                payload.CopyTo(rented);
                json = rented.AsSpan(0, (int)payload.Length);
            }

            target = JsonSerializer.Deserialize<SwapRequest>(json, ControlProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Mux]SYN 载荷解析失败 stream={StreamId}", streamId);
            target = null;
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        if (target == null || string.IsNullOrEmpty(target.Host) || target.Port <= 0
            || _streams.Count >= MuxFrame.MaxStreamCount)
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

        if (_onAccept != null)
        {
            // 交给业务层去连本地目标，不能阻塞读取循环
            _ = AcceptAsync(stream, target);
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

    private async ValueTask HandleDataAsync(uint streamId, ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        if (!_streams.TryGetValue(streamId, out var stream) || stream.IsDisposed)
        {
            // 流已关闭，数据丢弃（帧体已经在缓冲里被切掉，无需再读一遍）
            return;
        }

        bool closed;
        try
        {
            closed = await stream.ReceiveAsync(payload);
        }
        catch (Exception)
        {
            _streams.TryRemove(streamId, out _);
            stream.Dispose();
            return;
        }

        if (closed)
        {
            // 上层已经不再读取，这条流没有意义了
            _streams.TryRemove(streamId, out _);
            stream.Dispose();
        }
    }

    // ---------------- 帧写入 ----------------

    /// <summary>
    /// 把一批流数据写成若干 Data 帧。整批只取一次写锁、只 flush 一次，
    /// 于是「每帧一次内核写」变成「每批一次」，这是数据面吞吐的主要来源。
    /// 单帧大小仍受 <see cref="MuxFrame.MaxPayload"/> 约束，与旧对端保持兼容。
    /// </summary>
    internal async ValueTask WriteDataAsync(
        MuxStream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var output = _transport.Output;
            var rest = payload;

            while (!rest.IsEmpty)
            {
                var size = (int)Math.Min(rest.Length, MuxFrame.MaxPayload);

                // 一次申请「帧头 + 负载」的连续空间：整帧落在同一段内存里，
                // 头写进去后紧跟着拷负载，整帧只有一次用户态拷贝。
                var span = output.GetSpan(MuxFrame.HeaderSize + size);
                MuxFrame.WriteHeader(span, MuxFrameType.Data, stream.Id, size);
                rest.Span.Slice(0, size).CopyTo(span.Slice(MuxFrame.HeaderSize));
                output.Advance(MuxFrame.HeaderSize + size);

                rest = rest.Slice(size);
            }

            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 把一条 UDP 数据报的 [2 字节长度][负载] 和 MUX 帧头写进同一段传输缓冲。
    /// 载荷可以跨多个 MUX 帧，但 UDP 接收端看到的是连续字节流，线格式不变。
    /// 整条数据报只取一次写锁、只 flush 一次，负载只有一次用户态拷贝。
    /// </summary>
    internal async ValueTask WriteDatagramAsync(
        MuxStream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > UdpDatagramFramer.MaxDatagramSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "UDP 数据报超过最大长度");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var output = _transport.Output;
            var length = (ushort)payload.Length;
            var prefixWritten = 0;
            var payloadWritten = 0;

            while (prefixWritten < 2 || payloadWritten < payload.Length)
            {
                var remaining = (2 - prefixWritten) + (payload.Length - payloadWritten);
                var size = Math.Min(remaining, MuxFrame.MaxPayload);

                var span = output.GetSpan(MuxFrame.HeaderSize + size);
                MuxFrame.WriteHeader(span, MuxFrameType.Data, stream.Id, size);
                var destination = span.Slice(MuxFrame.HeaderSize, size);

                var offset = 0;
                if (prefixWritten == 0)
                {
                    destination[0] = (byte)(length >> 8);
                    destination[1] = (byte)length;
                    prefixWritten = 2;
                    offset = 2;
                }

                var take = Math.Min(payload.Length - payloadWritten, size - offset);
                if (take > 0)
                {
                    payload.Span.Slice(payloadWritten, take).CopyTo(destination.Slice(offset));
                    payloadWritten += take;
                }

                output.Advance(MuxFrame.HeaderSize + size);
            }

            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async ValueTask WriteFrameAsync(
        MuxFrameType type, uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            WriteFrameNoLock(type, streamId, payload.Span);
            await _transport.Output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>调用方必须已持有写锁。</summary>
    private void WriteFrameNoLock(MuxFrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        var output = _transport.Output;
        var span = output.GetSpan(MuxFrame.HeaderSize + payload.Length);
        MuxFrame.WriteHeader(span, type, streamId, payload.Length);

        if (!payload.IsEmpty)
        {
            payload.CopyTo(span.Slice(MuxFrame.HeaderSize));
        }

        output.Advance(MuxFrame.HeaderSize + payload.Length);
    }

    /// <summary>
    /// 窗口回补帧。这是数据面上唯一的「尽力而为」写：不阻塞读取循环、
    /// 不做线程池跳动、不为 4 字节负载分配数组。会话已关闭时静默丢弃。
    /// </summary>
    internal async ValueTask SendWindowUpdateAsync(uint streamId, int delta)
    {
        try
        {
            await _writeGate.WaitAsync(Closed);
            try
            {
                var output = _transport.Output;
                var span = output.GetSpan(MuxFrame.HeaderSize + 4);
                MuxFrame.WriteHeader(span, MuxFrameType.WindowUpdate, streamId, 4);
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(MuxFrame.HeaderSize, 4), delta);
                output.Advance(MuxFrame.HeaderSize + 4);
                await output.FlushAsync(Closed);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            // 会话已关闭或连接已断，无需处理
        }
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
}
