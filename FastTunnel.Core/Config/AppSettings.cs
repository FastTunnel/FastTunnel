using FastTunnel.Core.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FastTunnel.Core.Config
{
    public class AppSettings
    {
        public DefaultServerConfig ServerSettings { get; set; }

        public DefaultClientConfig ClientSettings { get; set; }
    }
}
