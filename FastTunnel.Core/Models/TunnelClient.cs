using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.Models
{
    public class TunnelClient
    {
        readonly LoginHandler _loginHandler;
        FastTunnelServer fastTunnelServer;
        ILogger logger;
        WebSocket webSocket;

        public TunnelClient(ILogger logger, WebSocket webSocket, FastTunnelServer fastTunnelServer)
        {
            this.webSocket = webSocket;
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
            this._loginHandler = new LoginHandler(logger, fastTunnelServer.proxyConfig);
        }

        public async Task ReviceAsync()
        {
            var buffer = new byte[512];
            var tunnelProtocol = new TunnelProtocol();

            while (true)
            {
                var res = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                var cmds = tunnelProtocol.HandleBuffer(buffer, 0, res.Count);
                if (cmds == null) continue;

                foreach (var item in cmds)
                {
                    if (!await HandleCmdAsync(webSocket, item))
                    {
                        return;
                    };
                }
            }
        }

        private async Task<bool> HandleCmdAsync(WebSocket webSocket, string lineCmd)
        {
            try
            {
                logger.LogInformation($"client：{lineCmd}");
                var cmds = lineCmd.Split("||");
                var type = cmds[0];

                TunnelMassage msg = null;
                IClientMessageHandler handler = null;
                switch (type)
                {
                    case "C_LogIn": // 登录
                        handler = _loginHandler;
                        msg = JsonSerializer.Deserialize<LogInMassage>(cmds[1]);
                        break;
                    default:
                        throw new Exception($"未知的通讯指令 {lineCmd}");
                }

                return await handler.HandlerMsg(fastTunnelServer, webSocket, msg);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"处理客户端消息失败：cmd={lineCmd}");
                return false;
            }
        }
    }
}
