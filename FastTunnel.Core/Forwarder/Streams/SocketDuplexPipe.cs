// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// 把一条客户端 Socket 包装成 <see cref="IDuplexPipe"/>，供多路复用会话读写。
/// 读写缓冲区取 16KB（默认 4KB），与 <see cref="Mux.MuxFrame.MaxPayload"/> 同量级，
/// 避免一帧数据被拆成多次 4KB 拷贝。
/// </summary>
internal sealed class SocketDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly NetworkStream stream;

    public SocketDuplexPipe(Socket socket)
    {
        stream = new NetworkStream(socket, ownsSocket: true);

        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: 16 * 1024,
            minimumReadSize: 8 * 1024));

        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(
            minimumBufferSize: 16 * 1024));
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
