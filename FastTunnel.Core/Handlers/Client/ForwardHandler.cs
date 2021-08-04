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
    public class ForwardHandler : IClientHandler
    {
        ILogger<ForwardHandler> _logger;

        public ForwardHandler(ILogger<ForwardHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg)
        {
            var msgs = msg.Split('|');

            _logger.LogDebug($"开始转发 {msgs[0]}");

            await Task.Yield();

            using Stream serverConn = await server(msgs[0], cleint);
            using Stream localConn = await local(msgs[0], msgs[1]);

            var taskX = serverConn.CopyToAsync(localConn, CancellationToken.None);
            var taskY = localConn.CopyToAsync(serverConn, CancellationToken.None);

            await Task.WhenAny(taskX, taskY);
        }

        private async Task<Stream> local(string requestId, string localhost)
        {
            _logger.LogDebug($"连接本地成功 {requestId}");
            var localConnecter = new DnsSocket(localhost.Split(":")[0], int.Parse(localhost.Split(":")[1]));
            await localConnecter.ConnectAsync();

            return new NetworkStream(localConnecter.Socket, ownsSocket: true);
        }

        private async Task<Stream> server(string requestId, FastTunnelClient cleint)
        {
            var connecter = new DnsSocket(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            await connecter.ConnectAsync();

            _logger.LogDebug($"连接server成功 {requestId}");
            Stream serverConn = new NetworkStream(connecter.Socket, ownsSocket: true);
            var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";

            var requestMsg = Encoding.ASCII.GetBytes(reverse);
            await serverConn.WriteAsync(requestMsg, CancellationToken.None);
            return serverConn;
        }
    }
}
