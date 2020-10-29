using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Handlers.Server;
using System.Collections.Concurrent;
using System;

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

        IListener client_listener;

        IListener http_listener;

        private void ListenClient()
        {
            client_listener = new AsyncListener(ServerSettings.BindAddr, ServerSettings.BindPort, _logger);
            client_listener.OnClientsChange += Client_listener_OnClientsChange;

            client_listener.Listen(new ClientDispatcher(this, _logger, ServerSettings));
            _logger.LogDebug($"监听客户端 -> {ServerSettings.BindAddr}:{ServerSettings.BindPort}");
        }

        private void Client_listener_OnClientsChange(System.Net.Sockets.Socket socket, int count, bool is_oofline)
        {
            if (is_oofline)
                _logger.LogInformation($"客户端 {socket.RemoteEndPoint} 已断开，当前连接数：{count}");
            else
                _logger.LogInformation($"客户端 {socket.RemoteEndPoint} 已连接，当前连接数：{count}");
        }

        private void ListenHttp()
        {
            http_listener = new AsyncListener(ServerSettings.BindAddr, ServerSettings.WebProxyPort, _logger);
            http_listener.Listen(new HttpDispatcher(this, _logger, ServerSettings));

            _logger.LogDebug($"监听HTTP -> {ServerSettings.BindAddr}:{ServerSettings.WebProxyPort}");
        }
    }
}
