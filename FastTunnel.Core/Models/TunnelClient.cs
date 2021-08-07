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

        public TunnelClient(ILogger logger, FastTunnelServer fastTunnelServer)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
            this._loginHandler = new LoginHandler(logger, fastTunnelServer.proxyConfig);
        }

        public TunnelClient SetSocket(WebSocket webSocket)
        {
            this.webSocket = webSocket;
            return this;
        }

        public async Task ReviceAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[128];
            var tunnelProtocol = new TunnelProtocol();

            while (true)
            {
                var res = await webSocket.ReceiveAsync(buffer, cancellationToken);
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
                logger.LogDebug($"client：{lineCmd}");

                var msg = JsonSerializer.Deserialize<LogInMassage>(lineCmd.Substring(1));
                return await _loginHandler.HandlerMsg(fastTunnelServer, webSocket, msg);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"处理客户端消息失败：cmd={lineCmd}");
                return false;
            }
        }
    }
}
