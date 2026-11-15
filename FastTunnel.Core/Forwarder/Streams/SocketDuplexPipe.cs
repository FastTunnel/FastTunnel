// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using FastTunnel.Core.Mux;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// 把一条客户端连接（明文 NetworkStream 或 TLS 后的 SslStream）包装成 <see cref="IDuplexPipe"/>，
/// 供多路复用会话读写。
///
/// 缓冲大小直接决定多路复用链路上的帧数与内核写次数：读写都取 64KB（默认 4KB），
/// 一次 flush 就能把多帧一起交给内核，而不是每 4KB 一次系统调用。
/// </summary>
internal sealed class SocketDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly Stream stream;

    public SocketDuplexPipe(Stream stream)
    {
        this.stream = stream;

        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: BufferSize,
            minimumReadSize: MuxFrame.MaxPayload));

        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(
            minimumBufferSize: BufferSize));
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Input.CompleteAsync();
        }
        catch
        {
            // 已完成或已出错
        }

        try
        {
            await Output.CompleteAsync();
        }
        catch
        {
        }

        await stream.DisposeAsync();
    }
}
