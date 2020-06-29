using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class SSHInfo<T>
    {
        public Socket Socket { get; set; }

        public SSHConfig SSHConfig { get; set; }

        public IListener<T> Listener { get; set; }
    }
}
