using FastTunnel.Core.Server;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Dispatchers
{
    public interface IListenerDispatcher
    {
        void Dispatch(AsyncUserToken token, string words);

        void DispatchAsync(Socket httpClient);
    }
}
