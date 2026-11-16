// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.Streams;

/// <summary>
/// 能把一条 UDP 数据报按 [2 字节大端长度][负载] 直接写进底层管道的实现。
///
/// 相比先拼一个 [长度+负载] 的临时缓冲再调用 Stream.WriteAsync，
/// 实现方可以把长度前缀和负载一起写进目标缓冲，少一次全量拷贝和一次数组租赁。
/// </summary>
internal interface IDatagramWriter
{
    ValueTask WriteDatagramAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
