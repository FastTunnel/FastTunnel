using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Client
{
    public interface IFastTunnelClient
    {
        void StartAsync(CancellationToken cancellationToken);
    }
}
