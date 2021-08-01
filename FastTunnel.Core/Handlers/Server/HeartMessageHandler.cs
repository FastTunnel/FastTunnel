using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Server
{
    public class HeartMessageHandler : IClientMessageHandler
    {
        public bool NeedRecive => true;

        public async Task<bool> HandlerMsg<T>(FastTunnelServer server, WebSocket client, T msg)
            where T : TunnelMassage
        {
            await client.SendCmdAsync(new Message<HeartMassage>() { MessageType = MessageType.Heart, Content = new HeartMassage { } });
            return NeedRecive;
        }
    }
}
