using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class LogMassage : TunnelMassage
    {
        public string Msg { get; set; }

        public LogMsgType MsgType { get; set; }

        public LogMassage(LogMsgType msgType, string msg)
        {
            this.Msg = msg;
            MsgType = msgType;
        }
    }

    public enum LogMsgType
    {
        Info,

        Error,

        Debug
    }
}
