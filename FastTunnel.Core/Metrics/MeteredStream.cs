// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Metrics;

/// <summary>
/// 计量用的 <see cref="Stream"/> 装饰器。端口转发路径的目标是 NetworkStream / MuxStream，
/// 走的是 <c>PipeReader.CopyToAsync(Stream)</c> 与 <c>Stream.CopyToAsync(Stream)</c>，
/// 这里把两个 WriteAsync 重载都拦下来，保证与 HTTP 路径用同一套计数和限速。
///
/// 不接管 <paramref name="inner"/> 的生命周期：调用方仍用 using 释放原始流。
/// </summary>
internal sealed class MeteredStream : Stream
{
    private readonly Stream inner;
    private readonly TrafficMeter meter;
    private readonly TrafficScope scope;
    private readonly TrafficDirection direction;
    private readonly TrafficDirection? readDirection;
    private readonly bool leaveOpen;

    public MeteredStream(
        Stream inner, TrafficMeter meter, TrafficScope scope, TrafficDirection direction,
        TrafficDirection? readDirection = null, bool leaveOpen = true)
    {
        this.inner = inner;
        this.meter = meter;
        this.scope = scope;
        this.direction = direction;
        this.readDirection = readDirection;
        this.leaveOpen = leaveOpen;
    }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        AccountRead(read, CancellationToken.None);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        AccountRead(read, CancellationToken.None);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        await AccountReadAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        await AccountReadAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Account(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Account(buffer.Length);
        inner.Write(buffer);
    }

    public override async Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await meter.AccountAsync(scope, direction, count, cancellationToken).ConfigureAwait(false);
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await meter.AccountAsync(scope, direction, buffer.Length, cancellationToken).ConfigureAwait(false);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private void Account(int bytes)
    {
        if (bytes > 0)
        {
            meter.AccountAsync(scope, direction, bytes, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
    }

    private void AccountRead(int bytes, CancellationToken cancellationToken)
    {
        if (bytes > 0 && readDirection.HasValue)
        {
            meter.AccountAsync(scope, readDirection.Value, bytes, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }
    }

    private ValueTask AccountReadAsync(int bytes, CancellationToken cancellationToken)
        => bytes > 0 && readDirection.HasValue
            ? meter.AccountAsync(scope, readDirection.Value, bytes, cancellationToken)
            : ValueTask.CompletedTask;

    /// <summary>只在 <c>leaveOpen: false</c> 时接管内层流生命周期，避免与调用方的 using 重复释放。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
