using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.Listener
{
    public class ClientListenerV2
    {
        ILogger _logger;

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        FastTunnelServer _fastTunnelServer;
        Server.Server server;

        readonly LoginHandler _loginHandler;
        readonly HeartMessageHandler _heartHandler;
        readonly SwapMessageHandler _swapMsgHandler;

        public ClientListenerV2(FastTunnelServer fastTunnelServer, IProxyConfigProvider proxyConfig, string ip, int port, ILogger logerr)
        {
            _fastTunnelServer = fastTunnelServer;
            _logger = logerr;
            this.ListenIp = ip;
            this.ListenPort = port;

            _loginHandler = new LoginHandler(_logger, proxyConfig);
            _heartHandler = new HeartMessageHandler();
            _swapMsgHandler = new SwapMessageHandler(_logger);

            server = new Server.Server(10000, 100, false, _logger);
        }

        public void Start()
        {
            IPAddress ipa = IPAddress.Parse(ListenIp);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);

            server.Init();
            server.Start(localEndPoint, "\n", handle);
            _logger.LogInformation($"监听客户端 -> {ListenIp}:{ListenPort}");
        }

        private bool handle(AsyncUserToken token, string words)
        {
            Message<JObject> msg;

            try
            {
                msg = JsonConvert.DeserializeObject<Message<JObject>>(words);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"【异常的指令】{words}");
                token.Socket.Close();
                return false;
            }

            try
            {
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

                handler.HandlerMsg(this._fastTunnelServer, token.Socket, msg);
                return handler.NeedRecive;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"处理客户端消息失败：msg={msg?.ToJson()}");
                token.Socket.Close();
                return false;
            }
        }

        public void Stop()
        {
        }

        public void Close()
        {
        }
    }
}
