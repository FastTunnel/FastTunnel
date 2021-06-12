using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Filters
{
    public interface IFastTunnelAuthenticationFilter : IFastTunntlfilter
    {
        bool Authentication(FastTunnelServer server, LogInMassage requet);
    }
}
