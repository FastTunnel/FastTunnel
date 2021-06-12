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

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer
    {
        public ConcurrentDictionary<string, NewRequest> newRequest = new ConcurrentDictionary<string, NewRequest>();
        public ConcurrentDictionary<string, WebInfo> WebList = new ConcurrentDictionary<string, WebInfo>();
        public ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>> SSHList = new ConcurrentDictionary<int, SSHInfo<SSHHandlerArg>>();

        public readonly IServerConfig ServerSettings;
        readonly ILogger _logger;
        ClientListener client_listener;
        HttpListener http_listener;

        public FastTunnelServer(ILogger logger, IServerConfig settings)
        {
            _logger = logger;
            ServerSettings = settings;
        }

        public void Run(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Starting =====");

            CheckSettins();

            ListenClient();
            ListenHttp();
        }

        private void CheckSettins()
        {
            if (string.IsNullOrEmpty(ServerSettings.WebDomain))
            {
                throw new Exception("[WebDomain] 配置不能为空");
            }
        }

        private void ListenClient()
        {
            client_listener = new ClientListener(this, ServerSettings.BindAddr, ServerSettings.BindPort, _logger);
            client_listener.OnClientsChange += Client_listener_OnClientsChange;
            client_listener.Start();

            _logger.LogInformation($"监听客户端 -> {ServerSettings.BindAddr}:{ServerSettings.BindPort}");
        }

        private void Client_listener_OnClientsChange(System.Net.Sockets.Socket socket, int count, bool is_oofline)
        {
            if (is_oofline)
                _logger.LogDebug($"客户端 {socket.RemoteEndPoint} 已断开，当前连接数：{count}");
            else
                _logger.LogDebug($"客户端 {socket.RemoteEndPoint} 已连接，当前连接数：{count}");
        }

        private void ListenHttp()
        {
            http_listener = new HttpListener(ServerSettings.BindAddr, ServerSettings.WebProxyPort, _logger);
            http_listener.Start(new HttpDispatcher(this, _logger, ServerSettings));

            _logger.LogInformation($"监听HTTP请求 -> {ServerSettings.BindAddr}:{ServerSettings.WebProxyPort}");
        }

        public void Stop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Stoping =====");

            // TODO:释放资源和线程
        }
    }
}
