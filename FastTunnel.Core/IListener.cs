using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core
{
    public interface IListener<T>
    {
        void Listen(Action<Socket, T> receiveClient);
    }
}
