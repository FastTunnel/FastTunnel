// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;

namespace FastTunnel.Core.Metrics;

/// <summary>
/// 计量用的 <see cref="PipeWriter"/> 装饰器。
///
/// 覆盖两条写入方式而不会重复计数：
/// ① <c>PipeReader.CopyToAsync(PipeWriter)</c> 走 <see cref="WriteAsync"/>；
/// ② <c>PumpSocketToPipeAsync</c> 走 <c>GetMemory → Advance → FlushAsync</c>，
///    在 <see cref="FlushAsync"/> 里结算 <c>Advance</c> 累计的字节。
/// 两条路径都把 <c>inner</c> 的写入原样委托出去，不改变 Pipe 的背压与零拷贝语义。
/// </summary>
internal sealed class MeteredPipeWriter : PipeWriter, IPipeWriterDecorator
{
    private readonly PipeWriter inner;
    private readonly TrafficMeter meter;
    private readonly TrafficMeter.ProxyCounters counters;
    private readonly TrafficScope scope;
    private readonly TrafficDirection direction;
    private int pending;

    public MeteredPipeWriter(
        PipeWriter inner, TrafficMeter meter, TrafficMeter.ProxyCounters counters,
        TrafficScope scope, TrafficDirection direction)
    {
        this.inner = inner;
        this.meter = meter;
        this.counters = counters;
        this.scope = scope;
        this.direction = direction;
    }

    public PipeWriter Inner => inner;

    public override void Advance(int bytes)
    {
        inner.Advance(bytes);
        if (bytes > 0)
        {
            Interlocked.Add(ref pending, bytes);
        }
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

    public override void CancelPendingFlush() => inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var bytes = Interlocked.Exchange(ref pending, 0);
        if (bytes > 0)
        {
            await meter.AccountAsync(scope, counters, direction, bytes, cancellationToken);
        }

        return await inner.FlushAsync(cancellationToken);
    }

    public override async ValueTask<FlushResult> WriteAsync(
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        if (source.Length > 0)
        {
            await meter.AccountAsync(scope, counters, direction, source.Length, cancellationToken);
        }

        return await inner.WriteAsync(source, cancellationToken);
    }
}
