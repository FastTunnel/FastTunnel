using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class WebInfo
    {
        public Socket Socket { get; set; }
        public WebConfig WebConfig { get; set; }
    }
}
