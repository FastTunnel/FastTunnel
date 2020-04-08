using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers.Client
{
    public class NewCustomerHandler : IClientHandler
    {
        public void HandlerMsg(FastTunnelClient cleint, Message<JObject> Msg)
        {
            var request = Msg.Content.ToObject<NewCustomerMassage>();
            var connecter = new Connecter(cleint._serverConfig.ServerAddr, cleint._serverConfig.ServerPort);
            connecter.Connect();
            connecter.Send(new Message<SwapMassage> { MessageType = MessageType.C_SwapMsg, Content = new SwapMassage(request.MsgId) });

            var localConnecter = new Connecter(request.WebConfig.LocalIp, request.WebConfig.LocalPort);
            localConnecter.Connect();

            new SocketSwap(connecter.Socket, localConnecter.Socket).StartSwap();
        }
    }
}
