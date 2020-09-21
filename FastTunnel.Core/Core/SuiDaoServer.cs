using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Handlers.Server;
using System.Collections.Concurrent;

namespace FastTunnel.Core.Core
{
    public class FastTunnelServer
    {
        public ConcurrentDictionary<string, NewRequest> newRequest = new ConcurrentDictionary<string, NewRequest>();
        public ConcurrentDictionary<string, WebInfo> WebList = new ConcurrentDictionary<string, WebInfo>();
        public ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>> SSHList = new ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>>();

        public readonly IServerConfig ServerSettings;
        readonly ILogger _logger;


        public FastTunnelServer(ILogger logger, IServerConfig settings)
        {
            _logger = logger;
            ServerSettings = settings;
        }

        public void Run()
        {
            _logger.LogDebug("FastTunnel Server Start");
            ListenClient();
            ListenHttp();
        }

        private void ListenClient()
        {
            IListener client_listener = new AsyncListener(ServerSettings.BindAddr, ServerSettings.BindPort, _logger);
            client_listener.Listen(new ClientDispatcher(this, _logger, ServerSettings));
            _logger.LogDebug($"监听客户端 -> {ServerSettings.BindAddr}:{ServerSettings.BindPort}");
        }

        private void ListenHttp()
        {
            IListener http_listener = new AsyncListener(ServerSettings.BindAddr, ServerSettings.WebProxyPort, _logger);
            http_listener.Listen(new HttpDispatcher(this, _logger, ServerSettings));

            _logger.LogDebug($"监听HTTP -> {ServerSettings.BindAddr}:{ServerSettings.WebProxyPort}");
        }
    }
}
