using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfig : IServerConfig
    {
        public string WebDomain { get; set; }

        public int WebProxyPort { get; set; } = 1270;

        public string[] WebAllowAccessIps { get; set; }

        public bool EnableForward { get; set; } = false;
    }
}
