using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Extensions
{
    public static class WebSocketExtensions
    {
        public static async Task SendCmdAsync(this WebSocket socket, MessageType type, string content, CancellationToken cancellationToken)
        {
            var buffer = Encoding.UTF8.GetBytes($"{(char)type}{content}\n");
            if (type != MessageType.LogIn && buffer.Length > 128)
                throw new ArgumentOutOfRangeException(nameof(content));

            await socket.SendAsync(buffer, WebSocketMessageType.Binary, false, cancellationToken);
        }
    }
}
