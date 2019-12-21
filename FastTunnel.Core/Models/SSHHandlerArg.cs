using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class SSHHandlerArg
    {
        public SSHConfig SSHConfig { get; internal set; }
        public Socket LocalClient { get; internal set; }
    }
}
