using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Server;
using Microsoft.AspNetCore.Http;
using FastTunnel.Core.Helper;
using System.IO;

namespace FastTunnel.Core.Core
{
    public class FastTunnelServer
    {
        public Dictionary<string, NewRequest> newRequest = new Dictionary<string, NewRequest>();
        public Dictionary<string, WebInfo> WebList = new Dictionary<string, WebInfo>();
        public Dictionary<int, SSHInfo<SSHHandlerArg>> SSHList = new Dictionary<int, SSHInfo<SSHHandlerArg>>();

        public IServerConfig _serverSettings { get; private set; }

        ILogger<FastTunnelServer> _logger;

        LoginHandler _loginHandler;
        HeartHandler _heartHandler;
        SwapMsgHandler _swapMsgHandler;

        public FastTunnelServer(ILogger<FastTunnelServer> logger)
        {
            _logger = logger;
            _loginHandler = new LoginHandler(logger);
            _heartHandler = new HeartHandler();
            _swapMsgHandler = new SwapMsgHandler(logger);
        }

        public void Run(IServerConfig settings)
        {
            _serverSettings = settings;
            _logger.LogDebug("FastTunnel Server Start");
            ListenFastTunnelClient();
            ListenCustomer();
        }

        private void ListenFastTunnelClient()
        {
            IListener<object> listener = new AsyncListener<object>(_serverSettings.BindAddr, _serverSettings.BindPort, _logger, null);
            listener.Listen(ReceiveClient);
            _logger.LogDebug($"监听客户端 -> {_serverSettings.BindAddr}:{_serverSettings.BindPort}");
        }

        private void ListenCustomer()
        {
            var listener = new AsyncListener<object>(_serverSettings.BindAddr, _serverSettings.WebProxyPort, _logger, null);
            listener.Listen(ReceiveCustomer);

            _logger.LogDebug($"监听HTTP -> {_serverSettings.BindAddr}:{_serverSettings.WebProxyPort}");
        }

        void ReceiveCustomer(Socket client, object _)
        {

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
                catch (SocketException ex)
                {
                    _logger.LogError(ex.Message);
                    if (client.Connected)
                        client.Close();
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                    throw;
                }

                try
                {
                    var endpoint = client.RemoteEndPoint as System.Net.IPEndPoint;
                    _logger.LogInformation($"Receive HTTP Request {endpoint.Address}:{endpoint.Port}");

                    if (_serverSettings.WebAllowAccessIps != null)
                    {
                        if (!_serverSettings.WebAllowAccessIps.Contains(endpoint.Address.ToString()))
                        {
                            HandlerHostNotAccess(client);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                }

                //将字节转换成字符串
                string words = Encoding.UTF8.GetString(buffer, 0, count);

                // 正则获取Host
                String Host = string.Empty;
                var pattern = @"[hH]ost:.+";
                var collection = Regex.Matches(words, pattern);
                if (collection.Count == 0)
                {
                    _logger.LogError($"Host异常：{words}");

                    // 返回错误页
                    HandlerHostRequired(client);
                    return;
                }
                else
                {
                    Host = collection[0].Value;
                }

                _logger.LogDebug(Host.Replace("\r", ""));
                var domain = Host.Split(":")[1].Trim();

                // 判断是否为ip
                if (IsIpDomian(domain))
                {
                    // 返回错误页
                    HandlerHostRequired(client);
                    return;
                }

                WebInfo web;
                if (!WebList.TryGetValue(domain, out web))
                {
                    HandlerClientNotOnLine(client, domain, buffer);
                    return;
                }

                if (!web.Socket.Connected)
                {
                    WebList.Remove(domain);
                    HandlerClientNotOnLine(client, domain, buffer);
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

                _logger.LogDebug($"OK");
                web.Socket.Send(new Message<NewCustomerMassage> { MessageType = MessageType.S_NewCustomer, Content = new NewCustomerMassage { MsgId = msgid, WebConfig = web.WebConfig } });
            }
            catch (Exception ex)
            {
                _logger.LogError("处理Http失败：" + ex);
                client.Close();
            }
        }

        private bool IsIpDomian(string domain)
        {
            return Regex.IsMatch(domain, @"^\d.\d.\d.\d.\d$");
        }

        private void HandlerHostNotAccess(Socket client)
        {
            _logger.LogDebug($"### NotAccessIps:'{client.RemoteEndPoint}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";

            byte[] responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_NotAccessIps);

            client.Send(Encoding.UTF8.GetBytes(statusLine));
            client.Send(Encoding.UTF8.GetBytes(responseHeader));
            client.Send(Encoding.UTF8.GetBytes("\r\n"));
            client.Send(responseBody);
            client.Close();
        }

        private void HandlerHostRequired(Socket client)
        {
            _logger.LogDebug($"### HostRequired:'{client.RemoteEndPoint}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";

            byte[] responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_HostRequired);

            client.Send(Encoding.UTF8.GetBytes(statusLine));
            client.Send(Encoding.UTF8.GetBytes(responseHeader));
            client.Send(Encoding.UTF8.GetBytes("\r\n"));
            client.Send(responseBody);
            client.Close();
        }

        private void HandlerClientNotOnLine(Socket client, string domain, byte[] buffer)
        {
            _logger.LogDebug($"### TunnelNotFound:'{domain}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";

            byte[] responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_NoTunnel);

            client.Send(Encoding.UTF8.GetBytes(statusLine));
            client.Send(Encoding.UTF8.GetBytes(responseHeader));
            client.Send(Encoding.UTF8.GetBytes("\r\n"));
            client.Send(responseBody);
            client.Close();
        }

        byte[] buffer = new byte[1024 * 1024];
        string temp = string.Empty;

        public void ReceiveClient(Socket client, object _)
        {
            //定义byte数组存放从客户端接收过来的数据
            int length;

            try
            {
                length = client.Receive(buffer);
                if (length == 0)
                {
                    try
                    {
                        client.Shutdown(SocketShutdown.Both);
                    }
                    catch
                    {
                        client.Close();
                    }

                    // 递归结束
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"接收客户端异常 -> 退出登录 {ex.Message}");

                if (client.Connected)
                {
                    client.Close();
                }
                return;
            }

            // 将字节转换成字符串
            string words = Encoding.UTF8.GetString(buffer, 0, length);
            words += temp;
            temp = string.Empty;

            try
            {
                int index = 0;
                bool needRecive = false;

                while (true)
                {
                    var firstIndex = words.IndexOf("\n");
                    if (firstIndex < 0)
                    {
                        temp += words;
                        ReceiveClient(client, _);
                        break;
                    }

                    var sub_words = words.Substring(index, firstIndex + 1);
                    var res = handle(sub_words, client);

                    if (res.NeedRecive)
                        needRecive = true;

                    words = words.Replace(sub_words, string.Empty);
                    if (string.IsNullOrEmpty(words))
                        break;
                }

                if (needRecive)
                {
                    ReceiveClient(client, _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                _logger.LogError($"handle fail msg：{words}");

                // throw;
                client.Send(new Message<LogMassage>() { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Error, ex.Message) });
            }
        }

        private IServerHandler handle(string words, Socket client)
        {
            Message<JObject> msg = JsonConvert.DeserializeObject<Message<JObject>>(words);

            IServerHandler handler = null;
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

            handler.HandlerMsg(this, client, msg);
            return handler;
        }
    }
}
