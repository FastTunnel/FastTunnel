using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core
{
    public interface IListener
    {
        string IP { get; }

        int Port { get; }

        void Listen(Action<Socket> receiveClient);

        void ShutdownAndClose();
    }
}
