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

        public Socket Client { get; set; }

        public Connecter(string v1, int v2)
        {
            this._ip = v1;
            this._port = v2;

            Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public void Connect()
        {
            IPAddress ip = IPAddress.Parse(_ip);
            IPEndPoint point = new IPEndPoint(ip, _port);

            Client.Connect(point);
        }

        private void Send(string msg)
        {
            Client.Send(Encoding.UTF8.GetBytes(msg));
        }

        public void Send<T>(Message<T> msg)
        {
            Send(msg.ToJson());
        }
    }
}
