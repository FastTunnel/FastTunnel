using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class WebInfo
    {
        public WebSocket Socket { get; set; }

        public WebConfig WebConfig { get; set; }
    }
}
