using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class SocketExtension
    {
        public static void SendCmd<T>(this Socket socket, Message<T> message)
            where T : TunnelMassage
        {
            try
            {
                socket.Send(Encoding.UTF8.GetBytes(message.ToJson() + "\n"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}
