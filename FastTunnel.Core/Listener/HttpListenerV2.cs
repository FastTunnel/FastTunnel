using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastTunnel.Core.Listener
{
    public class HttpListenerV2
    {
        ILogger _logger;

        public string ListenIp { get; protected set; }

        public int ListenPort { get; protected set; }

        IListenerDispatcher _requestDispatcher;
        public IList<Socket> ConnectedSockets = new List<Socket>();

        Server.Server server;

        public HttpListenerV2(string ip, int port, ILogger logger)
        {
            _logger = logger;
            this.ListenIp = ip;
            this.ListenPort = port;

            server = new Server.Server(1000, 512);
        }

        public void Start(IListenerDispatcher requestDispatcher, int backlog = 100)
        {
            _requestDispatcher = requestDispatcher;

            IPAddress ipa = IPAddress.Parse(ListenIp);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);

            server.Init();
            server.Start(localEndPoint, "\r\n\r\n", handle);
            _logger.LogInformation($"监听客户端 -> {ListenIp}:{ListenPort}");
        }

        private bool handle(AsyncUserToken token, string words)
        {
            Console.WriteLine(words);
            _requestDispatcher.Dispatch(token, words);
            return false;
        }

        public void Stop()
        {
        }
    }
}
