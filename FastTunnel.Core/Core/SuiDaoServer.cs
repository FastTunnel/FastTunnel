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
        public ServerConfig _serverSettings;

        ILogger<FastTunnelServer> _logger;

        LoginHandler _loginHandler;
        HeartHandler _heartHandler;
        SwapMsgHandler _swapMsgHandler;

        public FastTunnelServer(ServerConfig settings, ILogger<FastTunnelServer> logger, LoginHandler loginHandler, HeartHandler heartHandler, SwapMsgHandler swapMsgHandler)
        {
            _serverSettings = settings;
            _logger = logger;
            _loginHandler = loginHandler;
            _heartHandler = heartHandler;
            _swapMsgHandler = swapMsgHandler;
        }

        public void Run()
        {
            _logger.LogDebug("FastTunnel Server Start");
            ListenFastTunnelClient();
            ListenCustomer();
        }

        private void ListenFastTunnelClient()
        {
            var listener = new Listener<object>(_serverSettings.BindAddr, _serverSettings.BindPort, _logger, ReceiveClient, null);
            listener.Listen();
            _logger.LogDebug($"监听客户端 -> {_serverSettings.BindAddr}:{_serverSettings.BindPort}");
        }

        private void ListenCustomer()
        {
            var listener = new Listener<object>(_serverSettings.BindAddr, _serverSettings.ProxyPort_HTTP, _logger, ReceiveCustomer, null);
            listener.Listen();

            _logger.LogDebug($"监听HTTP -> {_serverSettings.BindAddr}:{_serverSettings.ProxyPort_HTTP}");
        }

        //接收消息
        void ReceiveCustomer(Socket client, object _)
        {
            _logger.LogDebug("Receive HTTP Request");

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

                //将字节转换成字符串
                string words = Encoding.UTF8.GetString(buffer, 0, count);

                // 正则获取Host
                String Host = string.Empty;
                var pattern = @"Host:.+";
                var collection = Regex.Matches(words, pattern);
                if (collection.Count == 0)
                {
                    _logger.LogError($"Host异常：{words}");
                    return;
                }
                else
                {
                    Host = collection[0].Value;
                }

                var domain = Host.Split(":")[1].Trim();

                _logger.LogDebug($"Host: {domain}");

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
                _logger.LogError(ex);
                client.Close();
            }
        }

        private void HandlerClientNotOnLine(Socket clientsocket, string domain, byte[] buffer)
        {
            _logger.LogDebug($"TunnelNotFound:'{domain}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";
            byte[] responseBody;

            var file = Path.Combine(AppContext.BaseDirectory, "Htmls", "TunnelNotFound.html");
            if (File.Exists(file))
                responseBody = FileHelper.GetBytesFromFile(file);
            else
                responseBody = Encoding.UTF8.GetBytes(TunnelResource.NoTunnelPage);

            clientsocket.Send(Encoding.UTF8.GetBytes(statusLine));
            clientsocket.Send(Encoding.UTF8.GetBytes(responseHeader));
            clientsocket.Send(Encoding.UTF8.GetBytes("\r\n"));
            clientsocket.Send(responseBody);
            clientsocket.Close();
        }

        byte[] buffer = new byte[1024 * 1024];

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

            try
            {
                if (HandleWords(words, client).NeedRecive)
                {
                    // 递归
                    ReceiveClient(client, _);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                _logger.LogError($"错误的消息内容：{words}");

                // throw;
                client.Send(new Message<LogMassage>() { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Error, ex.Message) });
            }
        }

        private IServerHandler HandleWords(string words, Socket client)
        {
            // 同时读到两个或多个指令
            var index = words.IndexOf("}{");
            if (index > 0)
            {
                _logger.LogError($"读到多个消息 {words}");
                var sub_words = words.Substring(0, index + 1);
                var left = words.Substring(index + 1);

                handle(sub_words, client);
                return HandleWords(left, client);
            }
            else
            {
                return handle(words, client);
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
