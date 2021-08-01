using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Client
{
    public class NewForwardHandler : IClientHandler
    {
        ILogger<NewForwardHandler> _logger;
        public NewForwardHandler(ILogger<NewForwardHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync<T>(FastTunnelClient cleint, T Msg)
            where T : TunnelMassage
        {
            var request_ssh = Msg as NewForwardMessage;
            await Task.Yield();

            var connecter_ssh = new DnsSocket(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            connecter_ssh.Connect();
            connecter_ssh.Send(new Message<SwapMassage> { MessageType = MessageType.C_SwapMsg, Content = new SwapMassage(request_ssh.MsgId) });

            var localConnecter_ssh = new DnsSocket(request_ssh.SSHConfig.LocalIp, request_ssh.SSHConfig.LocalPort);
            localConnecter_ssh.Connect();
            new SocketSwap(connecter_ssh.Socket, localConnecter_ssh.Socket, _logger, request_ssh.MsgId).StartSwap();
        }
    }
}
