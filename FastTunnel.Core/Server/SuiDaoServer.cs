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
        Dictionary<int, SSHInfo<SSHHandlerArg>> SSHList = new Dictionary<int, SSHInfo<SSHHandlerArg>>();
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
            var listener = new Listener<object>(serverSettings.BindAddr, serverSettings.BindPort, ReceiveClient, null);
            listener.Listen();
            _logger.Debug($"监听客户端 -> {serverSettings.BindAddr}:{serverSettings.BindPort}");
        }

        private void ListenCustomer()
        {
            var listener = new Listener<object>(serverSettings.BindAddr, serverSettings.ProxyPort_HTTP, ReceiveCustomer, null);
            listener.Listen();

            _logger.Debug($"监听HTTP -> {serverSettings.BindAddr}:{serverSettings.ProxyPort_HTTP}");
        }

        //接收消息
        void ReceiveCustomer(Socket client, object _)
        {
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
                    _logger.Error($"Host异常：{words}");
                    return;
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
                    _logger.Error($"客户端不存在:'{domain}'");
                    return;
                }

                if (!web.Socket.Connected)
                {
                    _logger.Error($"客户端已下线:'{domain}'");
                    WebList.Remove(domain);
                    return;
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
                _logger.Error(ex);
            }
        }

        private void ReceiveClient(Socket client, object _)
        {
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

            // 将字节转换成字符串
            string words = Encoding.UTF8.GetString(buffer, 0, length);
            var msg = JsonConvert.DeserializeObject<Message<object>>(words);

            _logger.Debug($"收到客户端指令：{msg.MessageType}");
            switch (msg.MessageType)
            {
                case MessageType.C_LogIn:
                    var requet = (msg.Content as JObject).ToObject<LogInRequest>();
                    if (requet.ClientConfig.Webs != null && requet.ClientConfig.Webs.Count() > 0)
                    {
                        foreach (var item in requet.ClientConfig.Webs)
                        {
                            var hostName = $"{item.SubDomain}.{serverSettings.Domain}".Trim();
                            if (WebList.ContainsKey(hostName))
                            {
                                _logger.Debug($"renew domain '{hostName}'");

                                WebList.Remove(hostName);
                                WebList.Add(hostName, new WebInfo { Socket = client, WebConfig = item });
                            }
                            else
                            {
                                _logger.Debug($"new domain '{hostName}'");
                                WebList.Add(hostName, new WebInfo { Socket = client, WebConfig = item });
                            }

                            client.Send(new Message<string> { MessageType = MessageType.Info, Content = $"TunnelForWeb is OK: you can visit {item.LocalIp}:{item.LocalPort} from http://{hostName}:{serverSettings.ProxyPort_HTTP}" });
                        }
                    }

                    if (requet.ClientConfig.SSH != null && requet.ClientConfig.SSH.Count() > 0)
                    {
                        foreach (var item in requet.ClientConfig.SSH)
                        {
                            if (SSHList.ContainsKey(item.RemotePort))
                                SSHList.Remove(item.RemotePort);

                            try
                            {
                                var ls = new Listener<SSHHandlerArg>("0.0.0.0", item.RemotePort, SSHHandler, new SSHHandlerArg { LocalClient = client, SSHConfig = item });
                                ls.Listen();

                                // listen success
                                SSHList.Add(item.RemotePort, new SSHInfo<SSHHandlerArg> { Listener = ls, Socket = client, SSHConfig = item });
                                _logger.Debug($"SSH proxy success on {item.RemotePort} -> {item.LocalIp}:{item.LocalPort}");
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"SSH proxy error on {item.RemotePort} -> {item.LocalIp}:{item.LocalPort}");
                                _logger.Error(ex);
                                continue;
                            }

                            client.Send(new Message<string> { MessageType = MessageType.Info, Content = $"Tunnel For ProxyPort is OK: {requet.ClientConfig.Common.ServerAddr}:{item.RemotePort}->{item.LocalIp}:{item.LocalPort}" });
                        }
                    }
                    break;
                case MessageType.C_Heart:
                    break;
                case MessageType.C_SwapMsg:
                    var msgId = (msg.Content as string);
                    NewRequest request;

                    if (!string.IsNullOrEmpty(msgId) && newRequest.TryGetValue(msgId, out request))
                    {
                        // Join
                        Task.Run(() =>
                        {
                            (new SocketSwap(request.CustomerClient, client))
                                .BeforeSwap(() => { if (request.Buffer != null) client.Send(request.Buffer); })
                                .StartSwap();
                        });
                    }
                    else
                    {
                        // 未找到，关闭连接
                        _logger.Error($"未找到请求:{msgId}");
                        client.Send(new Message<string> { MessageType = MessageType.Error, Content = $"未找到请求:{msgId}" });
                    }
                    break;
                case MessageType.S_NewCustomer:
                default:
                    throw new Exception("参数异常");
            }
        }

        private void SSHHandler(Socket client, SSHHandlerArg local)
        {
            var msgid = Guid.NewGuid().ToString();
            local.LocalClient.Send(new Message<NewSSHRequest> { MessageType = MessageType.S_NewSSH, Content = new NewSSHRequest { MsgId = msgid, SSHConfig = local.SSHConfig } });

            newRequest.Add(msgid, new NewRequest
            {
                CustomerClient = client,
            });
        }
    }
}
