using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class NewRequest
    {
        public Socket CustomerClient { get; set; }
        public byte[] Buffer { get; set; }
    }
}
