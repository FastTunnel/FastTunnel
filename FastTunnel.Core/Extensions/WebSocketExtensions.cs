// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;

namespace FastTunnel.Core.Extensions;

public static class WebSocketExtensions
{
    public static async Task SendCmdAsync(this WebSocket socket, MessageType type, string content, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted)
        {
            throw new SocketClosedException(socket.State.ToString());
        }

        var buffer = Encoding.UTF8.GetBytes($"{(char)type}{content}\n");
        await socket.SendAsync(buffer, WebSocketMessageType.Binary, false, cancellationToken);
    }
}
