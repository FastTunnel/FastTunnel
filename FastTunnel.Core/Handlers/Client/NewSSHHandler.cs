using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers.Client
{
    public class NewSSHHandler : IClientHandler
    {
        public void HandlerMsg(FastTunnelClient cleint, Message<JObject> Msg)
        {
            var request_ssh = Msg.Content.ToObject<NewSSHRequest>();
            var connecter_ssh = new Connecter(cleint._serverConfig.ServerAddr, cleint._serverConfig.ServerPort);
            connecter_ssh.Connect();
            connecter_ssh.Send(new Message<SwapMassage> { MessageType = MessageType.C_SwapMsg, Content = new SwapMassage(request_ssh.MsgId) });

            var localConnecter_ssh = new Connecter(request_ssh.SSHConfig.LocalIp, request_ssh.SSHConfig.LocalPort, 5000);
            localConnecter_ssh.Connect();

            new AsyncSocketSwap(connecter_ssh.Socket, localConnecter_ssh.Socket).StartSwapAsync();
        }
    }
}
