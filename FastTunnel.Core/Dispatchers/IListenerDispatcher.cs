using FastTunnel.Core.Server;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Dispatchers
{
    public interface IListenerDispatcher
    {
        void Dispatch(AsyncUserToken token, string words);

        void Dispatch(Socket httpClient);
    }
}
