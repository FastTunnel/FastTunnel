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

        public FastTunnelServer(ServerConfig settings, ILogger<FastTunnelServer> logger, LoginHandler loginHandler)
        {
            _serverSettings = settings;
            _logger = logger;
            _loginHandler = loginHandler;
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
            _logger.LogDebug("新的HTTP请求");

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
                    _logger.LogError($"客户端不存在:'{domain}'");
                    _logger.LogDebug(words);
                    return;
                }

                if (!web.Socket.Connected)
                {
                    _logger.LogError($"客户端已下线:'{domain}'");
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
                _logger.LogError(ex);
            }
        }

        public void ReceiveClient(Socket client, object _)
        {
            //定义byte数组存放从客户端接收过来的数据
            byte[] buffer = new byte[1024 * 1024];
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
            catch (Exception)
            {
                _logger.LogError($"client 退出登录");
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
                HandleWords(words, client);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                _logger.LogError($"收到客户端 words：{words}");

                // throw;
                client.Send(new Message<LogMsg>() { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Error, ex.Message) });
            }
        }

        private void HandleWords(string words, Socket client)
        {
            // 同时读到两个或多个指令
            var index = words.IndexOf("}{");
            if (index > 0)
            {
                var sub_words = words.Substring(0, index + 1);
                var left = words.Substring(index + 1);

                handle(sub_words, client);
                HandleWords(left, client);
            }
            else
            {
                handle(words, client);
            }
        }

        private IServerHandler handle(string words, Socket client)
        {
            Message<JObject> msg = JsonConvert.DeserializeObject<Message<JObject>>(words);

            IServerHandler handler = null;
            switch (msg.MessageType)
            {
                case MessageType.C_LogIn:
                    handler = _loginHandler;
                    break;
                case MessageType.Heart:

                    break;
                case MessageType.C_SwapMsg:
                case MessageType.S_NewCustomer:
                case MessageType.S_NewSSH:
                default:
                    handler = null;
                    break;
            }

            if (handler != null)
            {
                handler.HandlerMsg(this, client, msg);
                return handler;
            }

            HandleMsg(client, msg);
            return null;
        }

        private void HandleMsg(Socket client, Message<JObject> msg)
        {
            if (msg.MessageType != MessageType.Heart)
            {
                _logger.LogDebug($"收到客户端指令：{msg.MessageType}");
            }

            switch (msg.MessageType)
            {
                case MessageType.Heart:
                    client.Send(new Message<string>() { MessageType = MessageType.Heart, Content = null });

                    // 递归调用
                    ReceiveClient(client, null);
                    break;
                case MessageType.C_SwapMsg:
                    var SwapMsg = msg.Content.ToObject<SwapMsgModel>();
                    NewRequest request;

                    if (!string.IsNullOrEmpty(SwapMsg.msgId) && newRequest.TryGetValue(SwapMsg.msgId, out request))
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
                        _logger.LogError($"未找到请求:{SwapMsg.msgId}");
                        client.Send(new Message<LogMsg> { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Debug, $"未找到请求:{SwapMsg.msgId}") });
                    }

                    break;
                case MessageType.S_NewCustomer:
                default:
                    throw new Exception($"参数异常, 不支持消息类型 {msg.MessageType}");
            }
        }
    }
}
