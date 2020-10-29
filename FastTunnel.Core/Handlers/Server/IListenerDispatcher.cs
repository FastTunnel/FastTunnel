using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Handlers.Server
{
    public interface IListenerDispatcher
    {
        void Dispatch(Socket httpClient, Action<Socket> onOffLine);

        void Dispatch(Socket httpClient);
    }
}
