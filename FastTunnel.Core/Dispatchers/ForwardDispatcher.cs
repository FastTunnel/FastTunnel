using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Server;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Dispatchers
{
    public class ForwardDispatcher : IListenerDispatcher
    {
        private FastTunnelServer _server;
        private WebSocket _client;
        private ForwardConfig _config;

        public ForwardDispatcher(FastTunnelServer server, WebSocket client, ForwardConfig config)
        {
            _server = server;
            _client = client;
            _config = config;
        }

        public async Task DispatchAsync(Socket _socket)
        {
            var msgid = Guid.NewGuid().ToString();
            await _client.SendCmdAsync(new Message<NewForwardMessage> { MessageType = MessageType.S_NewSSH, Content = new NewForwardMessage { MsgId = msgid, SSHConfig = _config } });

            _server.RequestTemp.TryAdd(msgid, new NewRequest
            {
                CustomerClient = _socket,
            });
        }

        public void Dispatch(AsyncUserToken token, string words)
        {
            throw new NotImplementedException();
        }
    }
}
