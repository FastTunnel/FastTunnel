using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Server;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Dispatchers
{
    public class SSHDispatcher : IListenerDispatcher
    {
        private FastTunnelServer _server;
        private Socket _client;
        private SSHConfig _config;

        public SSHDispatcher(FastTunnelServer server, Socket client, SSHConfig config)
        {
            _server = server;
            _client = client;
            _config = config;
        }

        public void Dispatch(Socket _socket)
        {
            var msgid = Guid.NewGuid().ToString();
            _client.SendCmd(new Message<NewSSHRequest> { MessageType = MessageType.S_NewSSH, Content = new NewSSHRequest { MsgId = msgid, SSHConfig = _config } });
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
