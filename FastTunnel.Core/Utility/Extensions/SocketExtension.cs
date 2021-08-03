using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
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
            if (socket.Connected)
            {
                socket.Send(Encoding.UTF8.GetBytes(message.ToJson() + "\n"));
            }
            else
            {
                Console.WriteLine($"连接中断，消息发送失败：【{message.Content}】");
            }
        }
    }
}
