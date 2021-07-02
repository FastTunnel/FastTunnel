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
        private string _host;
        private int _port;

        public Socket Socket { get; set; }

        public Connecter(string v1, int v2)
        {
            this._host = v1;
            this._port = v2;

            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public void Connect()
        {
            DnsEndPoint dnsEndPoint = new DnsEndPoint(_host, _port);
            Socket.Connect(dnsEndPoint);
        }

        public void Send(byte[] data)
        {
            Socket.Send(data);
        }

        public void Send<T>(Message<T> msg)
            where T : TunnelMassage
        {
            Socket.SendCmd(msg);
        }

        public void Close()
        {
            Socket.Close();
        }
    }
}
