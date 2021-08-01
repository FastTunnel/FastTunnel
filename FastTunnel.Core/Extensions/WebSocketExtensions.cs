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
        public static async Task SendCmdAsync<T>(this WebSocket socket, Message<T> message,
            WebSocketMessageType webSocketMessage, bool end, CancellationToken cancellationToken)
            where T : TunnelMassage
        {
            var msg = Encoding.UTF8.GetBytes($"{message.MessageType.ToString()}||{message.Content.ToJson()}\n");
            await socket.SendAsync(msg, webSocketMessage, end, cancellationToken);
        }

        public static async Task SendCmdAsync<T>(this WebSocket socket, Message<T> message)
            where T : TunnelMassage
        {
            var msg = Encoding.UTF8.GetBytes($"{message.MessageType.ToString()}||{message.Content.ToJson()}\n");
            await socket.SendAsync(msg, WebSocketMessageType.Binary, false, CancellationToken.None);
        }
    }
}
