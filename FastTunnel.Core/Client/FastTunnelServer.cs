using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Handlers.Server;
using System.Collections.Concurrent;
using System;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Dispatchers;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer
    {
        /// <summary>
        /// 外部请求，需要定期清理
        /// TODO:是否可以实现LRU
        /// </summary>
        public ConcurrentDictionary<string, NewRequest> RequestTemp { get; private set; }
            = new ConcurrentDictionary<string, NewRequest>();

        public ConcurrentDictionary<string, WebInfo> WebList { get; private set; }
            = new ConcurrentDictionary<string, WebInfo>();

        public ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>> SSHList { get; private set; }
            = new ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>>();

        readonly ILogger _logger;
        readonly ClientListenerV2 clientListener;
        readonly HttpListenerV2 http_listener;
        public readonly IOptionsMonitor<DefaultServerConfig> serverOption;

        public FastTunnelServer(ILogger<FastTunnelServer> logger, IOptionsMonitor<DefaultServerConfig> serverSettings)
        {
            _logger = logger;
            serverOption = serverSettings;

            clientListener = new ClientListenerV2(this, "0.0.0.0", serverOption.CurrentValue.BindPort, _logger);
            http_listener = new HttpListenerV2("0.0.0.0", serverOption.CurrentValue.WebProxyPort, _logger);
        }

        public void Run()
        {
            _logger.LogInformation("===== FastTunnel Server Starting =====");

            listenClient();
            listenHttp();
        }

        private void listenClient()
        {
            clientListener.Start();
        }

        private void listenHttp()
        {
            http_listener.Start(new HttpDispatcherV2(this, _logger, serverOption));
        }

        public void Stop()
        {
            _logger.LogInformation("===== FastTunnel Server Stoping =====");

            clientListener.Stop();
            http_listener.Stop();
        }
    }
}
