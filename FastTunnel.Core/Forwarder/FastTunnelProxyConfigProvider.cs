using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.Forwarder
{
    public class FastTunnelProxyConfigProvider : IProxyConfigProvider
    {
        public IProxyConfig GetConfig()
        {
            return new FastTunnelProxyConfig();
        }
    }
}
