using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class SwapMsgModel
    {
        public string msgId { get; set; }

        public SwapMsgModel(string msgId)
        {
            this.msgId = msgId;
        }
    }
}
