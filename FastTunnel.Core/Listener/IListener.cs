using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Handlers.Server;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Listener
{
    public delegate void OnClientChangeLine(Socket socket, int count, bool is_offline);

    public interface IListener
    {
        string ListenIp { get; }

        int ListenPort { get; }

        void Start(int backlog = 100);

        void Stop();

        void Close();

    }
}
