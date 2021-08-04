using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Hosting.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Dispatchers
{
    public class ForwardDispatcher
    {
        private FastTunnelServer _server;
        private WebSocket _client;
        private ForwardConfig _config;

        public ForwardDispatcher(FastTunnelServer server, WebSocket client, ForwardConfig config)
        {
            _server = server;
            _client = client;
            _config = config;
        }

        public async void DispatchAsync(Socket _socket)
        {
            try
            {
                var msgid = Guid.NewGuid().ToString();
                await _client.SendCmdAsync(MessageType.Forward, $"{msgid}|{_config.LocalIp }:{_config.LocalPort}");

                var tcs = new TaskCompletionSource<Stream>();
                _server.ResponseTasks.TryAdd(msgid, tcs);

                using var stream1 = await tcs.Task;
                using var stream2 = new NetworkStream(_socket, true);
                await Task.WhenAll(stream1.CopyToAsync(stream2), stream2.CopyToAsync(stream1));
            }
            catch (Exception)
            {
            }
        }
    }
}
