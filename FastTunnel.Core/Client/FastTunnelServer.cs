using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Dispatchers;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.IO;
using Yarp.Sample;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer
    {
        public ConcurrentDictionary<string, TaskCompletionSource<Stream>> ResponseTasks { get; } = new();

        public ConcurrentDictionary<string, WebInfo> WebList { get; private set; } = new();

        public ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>> ForwardList { get; private set; }
            = new ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>>();

        public readonly IOptionsMonitor<DefaultServerConfig> ServerOption;
        public IProxyConfigProvider proxyConfig;

        public FastTunnelServer(IProxyConfigProvider proxyConfig, IOptionsMonitor<DefaultServerConfig> serverSettings)
        {
            ServerOption = serverSettings;
            this.proxyConfig = proxyConfig;
        }
    }
}
