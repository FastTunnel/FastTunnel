using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.IO;
using System.Net.Sockets;
using System.Threading;

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
            var request = Msg as NewForwardMessage;
            await Task.Yield();

            using var stream1 = await Server(cleint, request);
            using var stream2 = await local(request);

            await Task.WhenAll(stream1.CopyToAsync(stream2), stream2.CopyToAsync(stream1));
        }

        private async Task<Stream> local(NewForwardMessage request)
        {
            var localConnecter = new DnsSocket(request.SSHConfig.LocalIp, request.SSHConfig.LocalPort);
            await localConnecter.ConnectAsync();
            return new NetworkStream(localConnecter.Socket, true);
        }

        private async Task<Stream> Server(FastTunnelClient cleint, NewForwardMessage request)
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
