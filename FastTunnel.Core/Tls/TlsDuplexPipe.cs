// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
using System.Net.Security;
using System.Threading.Tasks;

namespace FastTunnel.Core.Tls;

/// <summary>
/// 把完成握手后的 <see cref="SslStream"/> 重新暴露成 <see cref="IDuplexPipe"/>，
/// 这样 TLS 之下的后续处理（协议识别、MUX 帧、HTTP 管道）完全感知不到加密层的存在。
/// </summary>
internal sealed class TlsDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly SslStream _ssl;

    public TlsDuplexPipe(SslStream ssl)
    {
        _ssl = ssl;

        // 缓冲区取 16KB，与 SocketDuplexPipe / MuxFrame 的负载量级保持一致，
        // 避免一帧明文被拆成多次 4KB 读写。
        Input = PipeReader.Create(ssl, new StreamPipeReaderOptions(
            bufferSize: 16 * 1024,
            minimumReadSize: 8 * 1024,
            leaveOpen: true));

        Output = PipeWriter.Create(ssl, new StreamPipeWriterOptions(
            minimumBufferSize: 16 * 1024,
            leaveOpen: true));
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
            // 对端已断开
        }

        try
        {
            await Output.CompleteAsync();
        }
        catch
        {
        }

        try
        {
            await _ssl.DisposeAsync();
        }
        catch
        {
        }
    }
}
