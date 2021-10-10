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
using Microsoft.AspNetCore.Hosting.Server;
using System.Net.Security;

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
            var socket = await DnsSocketFactory.ConnectAsync(localhost.Split(":")[0], int.Parse(localhost.Split(":")[1]));
            return new NetworkStream(socket, true);
        }

        private async Task<Stream> createRemote(string requestId, FastTunnelClient cleint, CancellationToken cancellationToken)
        {
            var socket = await DnsSocketFactory.ConnectAsync(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            Stream serverStream = new NetworkStream(socket, true);
            if (cleint.Server.Protocol == "wss")
            {
                var sslStream = new SslStream(serverStream, false, delegate { return true; });
                await sslStream.AuthenticateAsClientAsync(cleint.Server.ServerAddr);
                serverStream = sslStream;
            }

            var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";
            var requestMsg = Encoding.UTF8.GetBytes(reverse);
            await serverStream.WriteAsync(requestMsg, cancellationToken);
            return serverStream;
        }
    }
}
