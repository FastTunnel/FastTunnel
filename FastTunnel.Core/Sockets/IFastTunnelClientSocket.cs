using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets
{
    public interface IFastTunnelClientSocket
    {
        Task<int> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken);

        Task SendAsync<T>(Message<T> loginMsg, CancellationToken cancellationToken)
            where T : TunnelMassage;

        Task ConnectAsync(Uri url, CancellationToken cancellationToken);

        Task CloseAsync();
    }
}
