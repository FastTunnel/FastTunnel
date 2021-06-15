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

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer
    {
        public ConcurrentDictionary<string, NewRequest> newRequest = new ConcurrentDictionary<string, NewRequest>();
        public ConcurrentDictionary<string, WebInfo> WebList = new ConcurrentDictionary<string, WebInfo>();
        public ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>> SSHList = new ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>>();

        public readonly IServerConfig ServerSettings;
        readonly ILogger _logger;
        ClientListenerV2 clientListener;
        HttpListenerV2 http_listener;

        public FastTunnelServer(ILogger<FastTunnelServer> logger, IConfiguration configuration)
        {
            _logger = logger;
            ServerSettings = configuration.Get<AppSettings>().ServerSettings;

            clientListener = new ClientListenerV2(this, ServerSettings.BindAddr, ServerSettings.BindPort, _logger);
            http_listener = new HttpListenerV2(ServerSettings.BindAddr, ServerSettings.WebProxyPort, _logger);

            clientListener.OnClientsChange += Client_listener_OnClientsChange;
        }

        public void Run()
        {
            _logger.LogInformation("===== FastTunnel Server Starting =====");

            checkSettins();

            listenClient();
            listenHttp();
        }

        private void checkSettins()
        {
            if (string.IsNullOrEmpty(ServerSettings.WebDomain))
            {
                throw new Exception("[WebDomain] 配置不能为空");
            }
        }

        private void listenClient()
        {
            clientListener.Start();
        }

        private void listenHttp()
        {
            http_listener.Start(new HttpDispatcherV2(this, _logger, ServerSettings));
        }

        private void Client_listener_OnClientsChange(System.Net.Sockets.Socket socket, int count, bool is_oofline)
        {
            if (is_oofline)
                _logger.LogDebug($"客户端 {socket.RemoteEndPoint} 已断开，当前连接数：{count}");
            else
                _logger.LogDebug($"客户端 {socket.RemoteEndPoint} 已连接，当前连接数：{count}");
        }

        public void Stop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Stoping =====");

            // TODO:释放资源和线程
        }
    }
}
