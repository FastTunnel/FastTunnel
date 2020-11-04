using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Handlers.Server
{
    public class HeartMessageHandler : IClientMessageHandler
    {
        public bool NeedRecive => true;

        public void HandlerMsg(FastTunnelServer server, Socket client, Message<JObject> msg)
        {
            client.Send(new Message<HeartMassage>() { MessageType = MessageType.Heart, Content = null });
        }
    }
}
