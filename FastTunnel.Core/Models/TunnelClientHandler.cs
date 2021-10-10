using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Server;
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
    public class TunnelClientHandler
    {
        readonly ILoginHandler _loginHandler;
        FastTunnelServer fastTunnelServer;
        ILogger logger;

        public TunnelClientHandler(ILogger<TunnelClientHandler> logger, FastTunnelServer fastTunnelServer, ILoginHandler loginHandler)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
            this._loginHandler = loginHandler;
        }

        public async Task ReviceAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new byte[FastTunnelConst.MAX_CMD_LENGTH];
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
                return await _loginHandler.HandlerMsg(fastTunnelServer, webSocket, lineCmd.Substring(1));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"处理客户端消息失败：cmd={lineCmd}");
                return false;
            }
        }
    }
}
