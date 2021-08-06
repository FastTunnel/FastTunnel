using FastTunnel.Core.Listener;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class ForwardInfo<T>
    {
        public WebSocket Socket { get; set; }

        public ForwardConfig SSHConfig { get; set; }

        public PortProxyListener Listener { get; set; }
    }
}
