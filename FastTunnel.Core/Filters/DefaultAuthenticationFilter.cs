using FastTunnel.Core.Core;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Filters
{
    public class DefaultAuthenticationFilter : IFastTunnelAuthenticationFilter
    {
        public virtual bool Authentication(FastTunnelServer server, LogInMassage requet)
        {
            return true;
        }
    }
}
