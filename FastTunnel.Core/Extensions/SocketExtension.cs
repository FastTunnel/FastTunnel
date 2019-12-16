using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class SocketExtension
    {
        public static void Send<T>(this Socket socket, Message<T> message)
        {
            socket.Send(Encoding.UTF8.GetBytes(message.ToJson() + "\n"));
        }
    }
}
