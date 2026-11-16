// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.IO.Pipelines;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// 标记可以直接把 socket 读进它的 <see cref="PipeWriter.GetMemory"/>：
/// 这类 writer 的缓冲在连接关闭时不会归还共享池，调用方可以安全地跨 await 持有。
/// </summary>
internal interface IDirectBufferPipeWriter
{
}

/// <summary>
/// 计量装饰器等 PipeWriter 包装层实现它，让能力探测能看到内层 writer。
/// </summary>
internal interface IPipeWriterDecorator
{
    PipeWriter Inner { get; }
}

internal static class PipeWriterCapabilities
{
    internal static PipeWriter Unwrap(PipeWriter writer)
    {
        while (writer is IPipeWriterDecorator decorator)
        {
            writer = decorator.Inner;
        }

        return writer;
    }

    internal static bool SupportsDirectBuffer(PipeWriter writer)
        => Unwrap(writer) is IDirectBufferPipeWriter;
}
