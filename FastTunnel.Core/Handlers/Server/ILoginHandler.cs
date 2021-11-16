using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Server
{
    public interface ILoginHandler
    {
        Task<bool> HandlerMsg(FastTunnelServer fastTunnelServer, TunnelClient tunnelClient, string lineCmd);
    }
}
