// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Client.Sockets
{

    public class DnsSocketFactory
    {
        public static async Task<Socket> ConnectAsync(string host, int port)
        {
            var Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var dnsEndPoint = new DnsEndPoint(host, port);
            await Socket.ConnectAsync(dnsEndPoint);
            return Socket;
        }
    }

}
