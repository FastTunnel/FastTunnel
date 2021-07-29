using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.Forwarder
{
    public class FastTunnelProxyConfig : IProxyConfig
    {
        public FastTunnelProxyConfig()
            : this(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>())
        {
        }

        public FastTunnelProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            this.Routes = routes;
            this.Clusters = clusters;
            this.ChangeToken = new CancellationChangeToken(cancellationToken.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        private readonly CancellationTokenSource cancellationToken = new();
    }
}
