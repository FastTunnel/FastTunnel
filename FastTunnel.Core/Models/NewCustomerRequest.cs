using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class NewCustomerRequest
    {
        public string MsgId { get; set; }

        public WebConfig WebConfig { get; set; }
    }
}
