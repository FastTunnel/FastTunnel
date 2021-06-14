using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Server
{
    public class AsyncUserToken
    {
        public Socket Socket { get; set; }

        public string MassgeTemp { get; set; }
    }
}
