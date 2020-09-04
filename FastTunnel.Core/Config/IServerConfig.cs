using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public interface IServerConfig
    {
        string BindAddr { get; set; }

        int BindPort { get; set; }

        int ProxyPort_HTTP { get; set; }

        string Domain { get; set; }

        bool HasNginxProxy { get; set; }
    }
}
