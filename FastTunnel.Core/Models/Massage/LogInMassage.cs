using FastTunnel.Core.Config;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class LogInMassage : TunnelMassage
    {
        public IEnumerable<WebConfig> Webs { get; set; }

        public IEnumerable<SSHConfig> SSH { get; set; }
    }
}
