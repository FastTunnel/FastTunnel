using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class NewCustomerMassage : TunnelMassage
    {
        public string MsgId { get; set; }

        public WebConfig WebConfig { get; set; }
    }
}
