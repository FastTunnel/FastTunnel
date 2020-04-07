using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class Message<T>
    {
        public MessageType MessageType { get; set; }

        public T Content { get; set; }
    }

    public enum MessageType
    {
        // client use below
        C_LogIn,
        Heart,
        C_SwapMsg,

        // server use below
        S_NewCustomer,
        S_NewSSH,

        // twoway
        Log,
    }
}
