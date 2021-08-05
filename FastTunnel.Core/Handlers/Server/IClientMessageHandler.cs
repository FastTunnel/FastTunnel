using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers
{
    public interface IClientMessageHandler
    {
        bool NeedRecive { get; }

        Task<bool> HandlerMsg<T>(FastTunnelServer server, WebSocket client, T msg) where T : TunnelMassage;
    }
}