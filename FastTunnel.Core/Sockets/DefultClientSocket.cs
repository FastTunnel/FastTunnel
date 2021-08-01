using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets
{
    public class DefultClientSocket : IFastTunnelClientSocket
    {
        ClientWebSocket webSocket;

        public DefultClientSocket()
        {
            webSocket = new ClientWebSocket();
            webSocket.Options.RemoteCertificateValidationCallback = delegate { return true; };
            webSocket.Options.SetRequestHeader(HeaderConst.FASTTUNNEL_FLAG, "2.0.0");
            webSocket.Options.SetRequestHeader(HeaderConst.FASTTUNNEL_TYPE, HeaderConst.TYPE_CLIENT);
        }

        public async Task ConnectAsync(Uri url, CancellationToken cancellationToken)
        {
            await webSocket.ConnectAsync(url, cancellationToken);
        }

        public async Task CloseAsync()
        {
            if (webSocket.State == WebSocketState.Closed)
                return;

            await webSocket.CloseAsync(WebSocketCloseStatus.Empty, null, CancellationToken.None);
        }

        public async Task<int> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            var res = await webSocket.ReceiveAsync(buffer, cancellationToken);
            return res.Count;
        }

        public async Task SendAsync<T>(Message<T> msg, CancellationToken cancellationToken)
            where T : TunnelMassage
        {
            await webSocket.SendCmdAsync(msg, WebSocketMessageType.Binary, false, cancellationToken);
        }
    }
}
