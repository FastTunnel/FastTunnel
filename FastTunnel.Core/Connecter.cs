using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core
{
    public class Connecter
    {
        private string _ip;
        private int _port;

        public Socket Socket { get; set; }

        public Connecter(string v1, int v2, int? sendTimeout = null)
        {
            this._ip = v1;
            this._port = v2;

            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            if (sendTimeout.HasValue)
            {
                Socket.SendTimeout = sendTimeout.Value;
            }
            else
            {
                Socket.SendTimeout = 2000;
            }
        }

        public void Connect()
        {
            IPAddress ip = IPAddress.Parse(_ip);
            IPEndPoint point = new IPEndPoint(ip, _port);

            Socket.Connect(point);
        }

        public void Send(byte[] data)
        {
            Socket.Send(data);
        }

        public void Send<T>(Message<T> msg)
            where T : TunnelMassage
        {
            Socket.Send(msg);
        }

        public void Close()
        {
            Socket.Close();
        }
    }
}
