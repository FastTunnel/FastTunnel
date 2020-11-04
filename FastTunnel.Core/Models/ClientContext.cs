using FastTunnel.Core.Client;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class ClientContext
    {
        public FastTunnelServer CurrentServer { get; internal set; }
        public LogInMassage LogInMassage { get; internal set; }
    }
}
