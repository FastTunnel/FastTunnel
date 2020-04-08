using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class SwapMassage : TunnelMassage
    {
        public string msgId { get; set; }

        public SwapMassage(string msgId)
        {
            this.msgId = msgId;
        }
    }
}
