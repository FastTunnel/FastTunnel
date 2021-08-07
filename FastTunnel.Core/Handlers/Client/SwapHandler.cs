using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Utility.Extensions;
using System.Net.WebSockets;
using FastTunnel.Core.Forwarder;
using Microsoft;
using Microsoft.AspNetCore.DataProtection;
using System.Data.Common;

namespace FastTunnel.Core.Handlers.Client
{
    public class SwapHandler : IClientHandler
    {
        ILogger<SwapHandler> _logger;

        public SwapHandler(ILogger<SwapHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken)
        {
            var msgs = msg.Split('|');
            var requestId = msgs[0];
            var address = msgs[1];

            await Task.Yield();

            try
            {
                using Stream serverStream = await createRemote(requestId, cleint, cancellationToken);
                using Stream localStream = await createLocal(requestId, address, cancellationToken);

                var taskX = serverStream.CopyToAsync(localStream, cancellationToken);
                var taskY = localStream.CopyToAsync(serverStream, cancellationToken);

                await Task.WhenAny(taskX, taskY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Swap error {requestId}");
            }
        }

        private async Task<Stream> createLocal(string requestId, string localhost, CancellationToken cancellationToken)
        {
            var localConnecter = new DnsSocket(localhost.Split(":")[0], int.Parse(localhost.Split(":")[1]));
            await localConnecter.ConnectAsync();

            return new NetworkStream(localConnecter.Socket, ownsSocket: true);
        }

        private async Task<Stream> createRemote(string requestId, FastTunnelClient cleint, CancellationToken cancellationToken)
        {
            var connecter = new DnsSocket(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            await connecter.ConnectAsync();

            Stream serverConn = new NetworkStream(connecter.Socket, ownsSocket: true);
            var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\nConnection: keep-alive\r\n\r\n";

            var requestMsg = Encoding.ASCII.GetBytes(reverse);
            await serverConn.WriteAsync(requestMsg, cancellationToken);
            return serverConn;
        }
    }
}
