using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfig : IServerConfig
    {
        public string BindAddr { get; set; }

        public int BindPort { get; set; }

        public int ProxyPort_HTTP { get; set; } = 1270;

        public string Domain { get; set; }

        public bool HasNginxProxy { get; set; } = false;
    }
}
