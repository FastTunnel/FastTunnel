// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections.Features;

namespace FastTunnel.Core.Forwarder;

internal sealed class WebSocketStream : Stream
{
    private readonly Stream readStream;
    private readonly Stream wirteStream;
    private readonly IConnectionLifetimeFeature lifetimeFeature;

    public WebSocketStream(IConnectionLifetimeFeature lifetimeFeature, IConnectionTransportFeature transportFeature)
    {
        this.readStream = transportFeature.Transport.Input.AsStream();
        this.wirteStream = transportFeature.Transport.Output.AsStream();
        this.lifetimeFeature = lifetimeFeature;
    }

    public WebSocketStream(Stream stream)
    {
        this.readStream = stream;
        this.wirteStream = stream;
        this.lifetimeFeature = null;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        this.wirteStream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return this.wirteStream.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.readStream.Read(buffer, offset, count);
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        this.wirteStream.Write(buffer, offset, count);
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return this.readStream.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.readStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.wirteStream.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.wirteStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await this.wirteStream.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        this.lifetimeFeature?.Abort();
    }

    public override ValueTask DisposeAsync()
    {
        this.lifetimeFeature?.Abort();
        return ValueTask.CompletedTask;
    }
}
