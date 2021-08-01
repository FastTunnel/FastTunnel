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
using FastTunnel.Core.Server;
using System.Data.Common;

namespace FastTunnel.Core.Handlers.Client
{
    public class HttpRequestHandler : IClientHandler
    {
        ILogger<HttpRequestHandler> _logger;

        public HttpRequestHandler(ILogger<HttpRequestHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync<T>(FastTunnelClient cleint, T Msg) where T : TunnelMassage
        {
            var request = Msg as NewCustomerMassage;

            await Task.Yield();

            using Stream serverConn = await Server(cleint, request);
            using Stream localConn = await local(request);

            _logger.LogDebug($"开始转发 {request.MsgId}");
            var taskX = serverConn.CopyToAsync(localConn, CancellationToken.None);
            var taskY = localConn.CopyToAsync(serverConn, CancellationToken.None);

            await Task.WhenAny(taskX, taskY);
        }

        private async Task<Stream> local(NewCustomerMassage request)
        {
            _logger.LogDebug($"连接server成功 {request.MsgId}");
            var localConnecter = new DnsSocket(request.WebConfig.LocalIp, request.WebConfig.LocalPort);
            await localConnecter.ConnectAsync();

            _logger.LogDebug($"连接本地成功 {request.MsgId}");

            return new NetworkStream(localConnecter.Socket, ownsSocket: true);
        }

        private async Task<Stream> Server(FastTunnelClient cleint, NewCustomerMassage request)
        {
            var connecter = new DnsSocket(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            await connecter.ConnectAsync();
            Stream serverConn = new NetworkStream(connecter.Socket, ownsSocket: true);
            var reverse = $"PROXY /{request.MsgId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";

            var requestMsg = Encoding.ASCII.GetBytes(reverse);
            await serverConn.WriteAsync(requestMsg, CancellationToken.None);
            return serverConn;
        }
    }
}
