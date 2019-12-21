using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class NewSSHRequest
    {
        public string MsgId { get; set; }

        public SSHConfig SSHConfig { get; set; }
    }
}
