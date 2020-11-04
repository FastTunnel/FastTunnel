using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers.Client
{
    public class ClientHeartHandler : IClientHandler
    {
        public void HandlerMsg(FastTunnelClient cleint, Message<JObject> Msg)
        {
            cleint.lastHeart = DateTime.Now;
        }
    }
}
