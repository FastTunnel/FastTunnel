using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class LogMsg
    {
        public string Msg { get; set; }

        public LogMsgType MsgType { get; set; }

        public LogMsg(LogMsgType msgType, string msg)
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
