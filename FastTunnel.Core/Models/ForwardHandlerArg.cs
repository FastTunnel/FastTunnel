using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class ForwardHandlerArg
    {
        public ForwardConfig SSHConfig { get; internal set; }

        public Socket LocalClient { get; internal set; }
    }
}
