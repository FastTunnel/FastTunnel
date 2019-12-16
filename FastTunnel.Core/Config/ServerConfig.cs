using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class ServerConfig
    {
        public string BindAddr { get; set; }

        public int BindPort { get; set; }

        public int ProxyPort_HTTP { get; set; }

        public string Domain { get; set; }
    }
}
