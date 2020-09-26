using FastTunnel.Core.Core;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FastTunnel.Server.Filters
{
    public class TestAuthenticationFilter : IAuthenticationFilter
    {
        public bool Authentication(FastTunnelServer server, LogInMassage requet)
        {
            return true;
        }
    }
}
