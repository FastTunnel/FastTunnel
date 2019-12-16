using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Logger;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FastTunnel.Core.Server
{
    public class FastTunnelServer
    {
        Dictionary<string, WebInfo> WebList = new Dictionary<string, WebInfo>();
        Dictionary<string, NewRequest> newRequest = new Dictionary<string, NewRequest>();

        private ServerConfig serverSettings;
        ILogger _logger;

        public FastTunnelServer(ServerConfig serverSettings, ILogger logger)
        {
            _logger = logger;
            this.serverSettings = serverSettings;
        }

        public void Run()
        {
            ListenFastTunnelClient();
            ListenCustomer();
        }

        private void ListenFastTunnelClient()
        {
            var listener = new Listener(serverSettings.BindAddr, serverSettings.BindPort, ReceiveClient);
            listener.Listen();
            _logger.Debug($"监听客户端 -> {serverSettings.BindAddr}:{serverSettings.BindPort}");
        }

        private void ListenCustomer()
        {
            var listener = new Listener(serverSettings.BindAddr, serverSettings.ProxyPort_HTTP, ReceiveCustomer);
            listener.Listen();

            _logger.Debug($"监听HTTP -> {serverSettings.BindAddr}:{serverSettings.ProxyPort_HTTP}");
        }


        //接收消息
        void ReceiveCustomer(object o)
        {
            Socket client = o as Socket;
            _logger.Debug("新的HTTP请求");

            try
            {
                //定义byte数组存放从客户端接收过来的数据
                byte[] buffer = new byte[1024];

                int count;
                try
                {
                    count = client.Receive(buffer);
                    if (count == 0)
                    {
                        client.Close();
                        return;
                    }
                }
                catch (SocketException)
                {
                    if (client.Connected)
                        client.Close();
                    return;
                }
                catch (Exception)
                {
                    throw;
                }

                //将字节转换成字符串
                string words = Encoding.UTF8.GetString(buffer, 0, count);

                // 正则获取Host
                String Host = string.Empty;
                var pattern = @"Host:.+";
                var collection = Regex.Matches(words, pattern);
                if (collection.Count == 0)
                {
                    // TODO:
                    throw new Exception("不支持使用ip直接访问");
                }
                else
                {
                    Host = collection[0].Value;
                }

                var domain = Host.Split(":")[1].Trim();

                _logger.Debug($"Host: {domain}");

                WebInfo web;
                if (!WebList.TryGetValue(domain, out web))
                {
                    throw new ClienOffLineException("客户端不存在");
                }

                var msgid = Guid.NewGuid().ToString();

                byte[] bytes = new byte[count];
                Array.Copy(buffer, bytes, count);

                newRequest.Add(msgid, new NewRequest
                {
                    CustomerClient = client,
                    Buffer = bytes
                });

                web.Socket.Send(new Message<NewCustomerRequest> { MessageType = MessageType.S_NewCustomer, Content = new NewCustomerRequest { MsgId = msgid, WebConfig = web.WebConfig } });
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private void ReceiveClient(object obj)
        {
            Socket client = obj as Socket;
            //定义byte数组存放从客户端接收过来的数据
            byte[] buffer = new byte[1024 * 1024];

            int length;
            try
            {
                length = client.Receive(buffer);
            }
            catch (Exception ex)
            {
                if (client.Connected)
                {
                    client.Close();
                }
                return;
            }

            //将字节转换成字符串
            string words = Encoding.UTF8.GetString(buffer, 0, length);
            var msg = JsonConvert.DeserializeObject<Message<object>>(words);

            _logger.Debug($"收到客户端指令：{msg.MessageType}");
            switch (msg.MessageType)
            {
                case MessageType.C_LogIn:
                    var requet = (msg.Content as JObject).ToObject<LogInRequest>();
                    if (requet.WebList != null && requet.WebList.Count() > 0)
                    {
                        foreach (var item in requet.WebList)
                        {
                            var key = $"{item.SubDomain }.{serverSettings.Domain}";
                            if (WebList.ContainsKey(key))
                            {
                                WebList.Remove(key);
                                WebList.Add(key, new WebInfo { Socket = client, WebConfig = item });
                            }
                            else
                            {
                                WebList.Add(key, new WebInfo { Socket = client, WebConfig = item });
                            }
                        }
                    }
                    break;
                case MessageType.C_Heart:
                    break;
                case MessageType.C_NewRequest:
                    var msgId = (msg.Content as string);
                    NewRequest request;
                    if (newRequest.TryGetValue(msgId, out request))
                    {
                        // Join
                        Task.Run(() =>
                        {
                            (new SocketSwap(request.CustomerClient, client))
                                .BeforeSwap(() => { client.Send(request.Buffer); })
                                .StartSwap();
                        });
                    }
                    else
                    {
                        // 未找到，关闭连接
                        throw new Exception($"未找到请求:{msgId}");
                    }
                    break;
                case MessageType.S_NewCustomer:
                default:
                    throw new Exception("参数异常");
            }
        }
    }
}
