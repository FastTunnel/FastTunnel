using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Logger;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient
    {
        ClientConfig _clientConfig;

        Connecter connecter;

        ILogger _logger;

        public FastTunnelClient(ClientConfig clientConfig, ILogger logger)
        {
            _logger = logger;
            _clientConfig = clientConfig;
            connecter = new Connecter(_clientConfig.Common.ServerAddr, _clientConfig.Common.ServerPort);
        }

        public void Login()
        {
            //连接到的目标IP
            connecter.Connect();

            // 登录
            connecter.Send(new Message<LogInRequest> { MessageType = MessageType.C_LogIn, Content = new LogInRequest { WebList = _clientConfig.Webs } });

            _logger.Debug("登录成功");
            ReceiveServer(connecter.Client);
            _logger.Debug("客户端退出");
        }

        private void ReceiveServer(object obj)
        {
            var client = obj as Socket;
            byte[] buffer = new byte[1024];

            string lastBuffer = string.Empty;
            while (true)
            {
                int n = client.Receive(buffer);
                if (n == 0)
                {
                    client.Close();
                    break;
                }

                string words = Encoding.UTF8.GetString(buffer, 0, n);
                if (!string.IsNullOrEmpty(lastBuffer))
                {
                    words = lastBuffer + words;
                    lastBuffer = null;
                }

                _logger.Info($"收到服务端 {words}");
                var msgs = words.Split("\n");

                try
                {
                    foreach (var item in msgs)
                    {
                        if (string.IsNullOrEmpty(item))
                            continue;

                        if (item.EndsWith("}"))
                        {
                            HandleServerRequest(item);
                        }
                        else
                        {
                            lastBuffer = item;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.Message);
                    continue;
                }
            }
        }

        private void HandleServerRequest(string words)
        {
            Message<object> Msg;

            try
            {
                Msg = JsonConvert.DeserializeObject<Message<object>>(words);
                _logger.Info($"收到服务端指令 {Msg.MessageType}");

                switch (Msg.MessageType)
                {
                    case MessageType.C_Heart:
                        break;
                    case MessageType.S_NewCustomer:
                        var request = (Msg.Content as JObject).ToObject<NewCustomerRequest>();
                        var connecter = new Connecter(_clientConfig.Common.ServerAddr, _clientConfig.Common.ServerPort);
                        connecter.Connect();
                        connecter.Send(new Message<string> { MessageType = MessageType.C_NewRequest, Content = request.MsgId });

                        var localConnecter = new Connecter(request.WebConfig.LocalIp, request.WebConfig.LocalPort);
                        localConnecter.Connect();

                        new SocketSwap(connecter.Client, localConnecter.Client).StartSwap();
                        break;
                    case MessageType.C_NewRequest:
                    case MessageType.C_LogIn:
                    default:
                        throw new Exception("参数异常");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
                _logger.Error(words);
            }
        }
    }
}
