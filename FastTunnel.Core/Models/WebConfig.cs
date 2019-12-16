using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class WebConfig
    {
        public string SubDomain { get; set; }

        public string LocalIp { get; set; }

        public int LocalPort { get; set; }
    }
}
