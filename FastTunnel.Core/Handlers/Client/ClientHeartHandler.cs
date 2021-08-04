using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Client
{
    public class ClientHeartHandler : IClientHandler
    {
        public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg)
        {
            cleint.lastHeart = DateTime.Now;
            await Task.Yield();
        }
    }
}
