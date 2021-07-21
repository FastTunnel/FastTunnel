using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfig : IServerConfig
    {
        public int BindPort { get; set; }

        public string WebDomain { get; set; }

        public int WebProxyPort { get; set; } = 1270;

        public string[] WebAllowAccessIps { get; set; }

        public bool WebHasNginxProxy { get; set; } = false;

        public bool SSHEnabled { get; set; } = false;
    }
}
