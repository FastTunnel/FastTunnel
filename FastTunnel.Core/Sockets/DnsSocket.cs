using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets
{
    public class DnsSocket
    {
        private string _host;
        private int _port;

        public Socket Socket { get; }

        public DnsSocket(string host, int port)
        {
            this._host = host;
            this._port = port;

            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Socket.NoDelay = true;
        }

        public async Task ConnectAsync()
        {
            DnsEndPoint dnsEndPoint = new DnsEndPoint(_host, _port);
            await Socket.ConnectAsync(dnsEndPoint);
        }
    }
}
