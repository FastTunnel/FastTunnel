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

        public Socket Socket { get; set; }

        public DnsSocket(string v1, int v2)
        {
            this._host = v1;
            this._port = v2;

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
