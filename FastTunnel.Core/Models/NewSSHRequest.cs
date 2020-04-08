using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class NewSSHRequest : TunnelMassage
    {
        public string MsgId { get; set; }

        public SSHConfig SSHConfig { get; set; }
    }
}
