using FastTunnel.Core.Handlers.Server;
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

        void Listen(IListenerDispatcher requestDispatcher);

        void ShutdownAndClose();

        event OnClientChangeLine OnClientsChange;
    }
}
