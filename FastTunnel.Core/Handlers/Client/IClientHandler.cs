using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers.Client
{
    public interface IClientHandler
    {
        public void HandlerMsg(FastTunnelClient cleint, Message<JObject> Msg);
    }
}
