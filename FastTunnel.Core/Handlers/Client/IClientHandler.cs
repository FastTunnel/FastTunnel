using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Client
{
    public interface IClientHandler
    {
        Task HandlerMsgAsync(FastTunnelClient cleint, string msg);
    }
}
