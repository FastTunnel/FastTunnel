using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Handlers.Server
{
    public class ClientDispatcher : IListenerDispatcher
    {
        readonly ILogger _logger;
        readonly IServerConfig _serverSettings;
        readonly FastTunnelServer _fastTunnelServer;

        readonly LoginMessageHandler _loginHandler;
        readonly HeartMessageHandler _heartHandler;
        readonly SwapMessageHandler _swapMsgHandler;

        public ClientDispatcher(FastTunnelServer fastTunnelServer, ILogger logger, IServerConfig serverSettings)
        {
            _logger = logger;
            _serverSettings = serverSettings;
            _fastTunnelServer = fastTunnelServer;

            _loginHandler = new LoginMessageHandler(logger);
            _heartHandler = new HeartMessageHandler();
            _swapMsgHandler = new SwapMessageHandler(logger);
        }

        byte[] buffer = new byte[1024 * 1024];
        string temp = string.Empty;

        public void Dispatch(Socket client)
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
                    finally
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

            _logger.LogDebug($"revice from client: {words}");

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
                        Dispatch(client);
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
                    Dispatch(client);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                _logger.LogError($"handle fail msg：{words}");

                // throw;
                client.Send(new Message<LogMassage>() { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Error, ex.Message) });
                Dispatch(client);
            }
        }

        private IClientMessageHandler handle(string words, Socket client)
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
            return handler;
        }
    }
}
