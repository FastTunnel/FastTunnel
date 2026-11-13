// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;

namespace FastTunnel.Core.Extensions;

public static class WebSocketExtensions
{
    /// <summary>
    /// WebSocket 不允许并发 SendAsync。心跳、Swap 通知、日志会从不同任务同时发送，
    /// 用弱引用表按连接串行化，既避免消息交错，也不会阻止连接被回收。
    /// </summary>
    private static readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> SendGates = new();

    /// <summary>发送裸文本帧，保留给兼容路径使用。</summary>
    public static Task SendCmdAsync(this WebSocket socket, MessageType type, string content, CancellationToken cancellationToken)
        => SendRawAsync(socket, type, content, cancellationToken);

    /// <summary>发送结构化消息，序列化为 JSON 后写入 payload。</summary>
    public static Task SendMessageAsync<T>(this WebSocket socket, MessageType type, T payload, CancellationToken cancellationToken)
        => SendRawAsync(socket, type, JsonSerializer.Serialize(payload, ControlProtocol.JsonOptions), cancellationToken);

    private static async Task SendRawAsync(WebSocket socket, MessageType type, string content, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            throw new SocketClosedException(socket.State.ToString());
        }

        var gate = SendGates.GetValue(socket, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var buffer = Encoding.UTF8.GetBytes($"{(char)type}{content}\n");
            // 一条逻辑消息即一个完整帧；接收端按 \n 重组，不再依赖分片语义。
            await socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
