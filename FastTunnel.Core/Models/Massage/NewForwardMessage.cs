using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class NewForwardMessage : TunnelMassage
    {
        public string MsgId { get; set; }

        public ForwardConfig SSHConfig { get; set; }
    }
}
