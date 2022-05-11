// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

namespace FastTunnel.Core.Forwarder.MiddleWare;

internal class DuplexPipeStream : Stream
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly bool _throwOnCancelled;
    private volatile bool _cancelCalled;

    public DuplexPipeStream(PipeReader input, PipeWriter output, bool throwOnCancelled = false)
    {
        _input = input;
        _output = output;
        _throwOnCancelled = throwOnCancelled;
    }

    public void CancelPendingRead()
    {
        _cancelCalled = true;
        _input.CancelPendingRead();
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length
    {
        get
        {
            throw new NotSupportedException();
        }
    }

    public override long Position
    {
        get
        {
            throw new NotSupportedException();
        }
        set
        {
            throw new NotSupportedException();
        }
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
        var vt = ReadAsyncInternal(new Memory<byte>(buffer, offset, count), default);
        return vt.IsCompleted ?
            vt.Result :
            vt.AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        return ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        return ReadAsyncInternal(destination, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override Task WriteAsync(byte[]? buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _output.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).GetAsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        return _output.WriteAsync(source, cancellationToken).GetAsValueTask();
    }

    public override void Flush()
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _output.FlushAsync(cancellationToken).GetAsTask();
    }

#if NET6_0_OR_GREATER
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<int> ReadAsyncInternal(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result;
            ReadOnlySequence<byte> readableBuffer;

            result = await _input.ReadAsync(cancellationToken);
            readableBuffer = result.Buffer;

            try
            {
                if (_throwOnCancelled && result.IsCanceled && _cancelCalled)
                {
                    // Reset the bool
                    _cancelCalled = false;
                    throw new OperationCanceledException();
                }

                if (!readableBuffer.IsEmpty)
                {
                    // buffer.Count is int
                    var count = (int)Math.Min(readableBuffer.Length, destination.Length);
                    readableBuffer = readableBuffer.Slice(0, count);
                    Console.WriteLine($"[{this.GetHashCode()}读取]{Encoding.UTF8.GetString(readableBuffer)}");
                    readableBuffer.CopyTo(destination.Span);
                    return count;
                }

                if (result.IsCompleted)
                {
                    return 0;
                }
            }
            finally
            {
                _input.AdvanceTo(readableBuffer.End, readableBuffer.End);
            }
        }
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        return TaskToApm.End<int>(asyncResult);
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        TaskToApm.End(asyncResult);
    }
}
