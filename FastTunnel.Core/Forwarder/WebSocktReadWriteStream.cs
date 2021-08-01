using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder
{
    public class WebSocktReadWriteStream : IReadWriteStream
    {
        WebSocket webSocket;
        public WebSocktReadWriteStream(WebSocket webSocket)
        {
            this.webSocket = webSocket;
        }

        public int Read(byte[] buffer)
        {
            if (this.webSocket.CloseStatus.HasValue)
            {
                return 0;
            }

            return webSocket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult().Count;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            this.webSocket.SendAsync(new ArraySegment<byte>(buffer, offset, count), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
    }
}
