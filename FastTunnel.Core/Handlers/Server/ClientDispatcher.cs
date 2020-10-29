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
        Action<Socket> offLineAction;

        public ClientDispatcher(FastTunnelServer fastTunnelServer, ILogger logger, IServerConfig serverSettings)
        {
            _logger = logger;
            _serverSettings = serverSettings;
            _fastTunnelServer = fastTunnelServer;

            _loginHandler = new LoginMessageHandler(logger);
            _heartHandler = new HeartMessageHandler();
            _swapMsgHandler = new SwapMessageHandler(logger);
        }

        string temp = string.Empty;

        public void Dispatch(Socket client)
        {
            var reader = new DataReciver(client);
            reader.OnComplete += Reader_OnComplete;
            reader.OnError += Reader_OnError;
            reader.OnReset += Reader_OnReset;
            reader.ReciveOneAsync();
        }

        private void Reader_OnReset(DataReciver send, Socket socket, SocketAsyncEventArgs e)
        {
            offLineAction(socket);
        }

        private void Reader_OnError(DataReciver send, SocketAsyncEventArgs e)
        {
            _logger.LogError("接收客户端数据异常 {0}", e.SocketError);
        }

        private void Reader_OnComplete(DataReciver reader, byte[] buffer, int offset, int count)
        {
            var words = Encoding.UTF8.GetString(buffer, offset, count);
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
                        reader.ReciveOneAsync();
                        break;
                    }

                    var sub_words = words.Substring(index, firstIndex + 1);
                    var res = handle(sub_words, reader.Socket);

                    if (res.NeedRecive)
                        needRecive = true;

                    words = words.Replace(sub_words, string.Empty);
                    if (string.IsNullOrEmpty(words))
                        break;
                }

                if (needRecive)
                {
                    reader.ReciveOneAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                _logger.LogError($"handle fail msg：{words}");

                // throw;
                reader.Socket.Send(new Message<LogMassage>() { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Error, ex.Message) });
                reader.ReciveOneAsync();
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

        public void Dispatch(Socket httpClient, Action<Socket> onOffLine)
        {
            offLineAction = onOffLine;
            Dispatch(httpClient);
        }
    }
}
