using FastTunnel.Core.Client;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FastTunnel.Server.Filters
{
    public class TestAuthenticationFilter : IFastTunnelAuthenticationFilter
    {
        public bool Authentication(FastTunnelServer server, LogInMassage requet)
        {
            return (requet.CustomInfo as JObject)["Token"].ToString() == "123";
        }
    }
}
