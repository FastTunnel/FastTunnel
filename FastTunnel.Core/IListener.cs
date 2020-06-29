using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core
{
    public interface IListener<T>
    {
        string IP { get; set; }

        int Port { get; set; }

        void Listen(Action<Socket, T> receiveClient);

        void ShutdownAndClose();
    }
}
