using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public struct Message<T>
    {
        public MessageType MessageType { get; set; }

        public T Content { get; set; }
    }

    public enum MessageType : byte
    {
        LogIn = 1, // client
        SwapMsg = 2,
        Forward = 3,
        Log = 4,
    }
}
