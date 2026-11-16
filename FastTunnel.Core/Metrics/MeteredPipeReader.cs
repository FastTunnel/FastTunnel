// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Metrics;

/// <summary>
/// 计量用的 PipeReader 装饰器：在把缓冲交给消费者之前按字节数结算流量与限速。
///
/// UDP 转发会话的消费模式是「一次 ReadAsync 拿到的帧全部处理并 AdvanceTo」，
/// 因此这里按缓冲区长度结算，既不会漏计也不会在热路径上引入额外的异步跳转。
/// </summary>
internal sealed class MeteredPipeReader : PipeReader
{
    private readonly PipeReader inner;
    private readonly TrafficMeter meter;
    private readonly TrafficMeter.ProxyCounters counters;
    private readonly TrafficScope scope;
    private readonly TrafficDirection direction;
    private ReadOnlySequence<byte> lastAccounted;

    public MeteredPipeReader(
        PipeReader inner, TrafficMeter meter, TrafficMeter.ProxyCounters counters,
        TrafficScope scope, TrafficDirection direction)
    {
        this.inner = inner;
        this.meter = meter;
        this.counters = counters;
        this.scope = scope;
        this.direction = direction;
    }

    public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        => inner.AdvanceTo(consumed, examined);

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result))
        {
            return false;
        }

        if (!result.Buffer.Equals(lastAccounted))
        {
            lastAccounted = result.Buffer;
            meter.AccountAsync(scope, counters, direction, (int)result.Buffer.Length, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }

        return true;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        => ReadCoreAsync(cancellationToken);

    private async ValueTask<ReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Buffer.IsEmpty && !result.Buffer.Equals(lastAccounted))
        {
            lastAccounted = result.Buffer;
            await meter.AccountAsync(
                scope, counters, direction, (int)result.Buffer.Length, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
