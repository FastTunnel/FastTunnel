using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FastTunnel.Core.Listener
{
    public class ClientListenerV2 : IListener
    {
        ILogger _logger;

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        public event OnClientChangeLine OnClientsChange;

        bool shutdown = false;
        Socket listenSocket;
        public IList<ClientConnection> ConnectedSockets = new List<ClientConnection>();
        FastTunnelServer _fastTunnelServer;
        Server.Server server;

        readonly LoginHandler _loginHandler;
        readonly HeartMessageHandler _heartHandler;
        readonly SwapMessageHandler _swapMsgHandler;

        public ClientListenerV2(FastTunnelServer fastTunnelServer, string ip, int port, ILogger logerr)
        {
            _fastTunnelServer = fastTunnelServer;
            _logger = logerr;
            this.ListenIp = ip;
            this.ListenPort = port;

            _loginHandler = new LoginHandler(_logger);
            _heartHandler = new HeartMessageHandler();
            _swapMsgHandler = new SwapMessageHandler(_logger);

            server = new Server.Server(1000, 1024);
        }

        public void Start(int backlog = 100)
        {
            IPAddress ipa = IPAddress.Parse(ListenIp);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);

            server.Init();
            server.Start(localEndPoint, handle);
            _logger.LogInformation($"监听客户端 -> {ListenIp}:{ListenPort}");
        }

        private bool handle(Socket client, string words)
        {
            Message<JObject> msg = JsonConvert.DeserializeObject<Message<JObject>>(words);

            IClientMessageHandler handler = null;
            switch (msg.MessageType)
            {
                case MessageType.C_LogIn: // 登录
                    handler = _loginHandler;
                    break;
                case MessageType.Heart:   // 心跳
                    handler = _heartHandler;
                    break;
                case MessageType.C_SwapMsg: // 交换数据
                    handler = _swapMsgHandler;
                    break;
                default:
                    throw new Exception($"未知的通讯指令 {msg.MessageType}");
            }

            handler.HandlerMsg(this._fastTunnelServer, client, msg);
            return handler.NeedRecive;
        }

        public void Stop()
        {
        }

        private void HandleNewClient(Socket socket)
        {
            // 此时的客户端可能有两种 1.登录的客户端 2.交换请求的客户端
            var client = new ClientConnection(_fastTunnelServer, socket, _logger);
            ConnectedSockets.Add(client);

            // 接收客户端消息
            client.StartRecive();
        }


        public void Close()
        {
        }
    }
}
