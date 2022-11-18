// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using BeetleX;
using BeetleX.EventArgs;
using FastTunnel.Core.Handlers.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using IServer = BeetleX.IServer;

namespace FastTunnel.Core.Listener
{
    public class PortProxyListenerV2
    {
        ILogger _logerr;

        public string ListenIp { get; private set; }

        public int ListenPort { get; private set; }

        public IServer Server { get; set; }

        int m_numConnectedSockets;

        private IServer server;

        bool shutdown;
        ForwardDispatcher _requestDispatcher;
        WebSocket client;

        // string ip, int port, ILogger logerr, WebSocket client
        public PortProxyListenerV2()
        {
            //IPAddress ipa = IPAddress.Parse(ListenIp);
            //IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);
        }

        public void Start(ForwardDispatcher requestDispatcher, string host, int port, ILogger logger, WebSocket webSocket)
        {
            this.client = webSocket;
            this._logerr = logger;
            this.ListenIp = host;
            this.ListenPort = port;

            shutdown = false;
            _requestDispatcher = requestDispatcher;

            server = SocketFactory.CreateTcpServer<TcpServerHandler>();
            var handler = server.Handler as TcpServerHandler;
            handler.Sethanler(this);
            server.Options.DefaultListen.Port = port;
            server.Options.DefaultListen.Host = host;
            server.Open();
        }

        //protected override void OnReceiveMessage(IServer server, ISession session, object message)
        //{
        //    base.OnReceiveMessage(server, session, message);
        //}

        private void ProcessAcceptAsync(SocketAsyncEventArgs e)
        {
            // 将此客户端交由Dispatcher进行管理
        }

        internal async void Process(SessionReceiveEventArgs e)
        {
            //var pipeStream = e.Session.Stream.ToPipeStream();
            //if (pipeStream.TryReadLine(out string name))
            //{
            //    Console.WriteLine(name);
            //    e.Stream.ToPipeStream().WriteLine("hello " + name);
            //    e.Stream.Flush();
            //}

            await _requestDispatcher.DispatchAsync(e.Stream, client);
        }

        public void Stop()
        {
            if (shutdown)
                return;

            try
            {
                server.Dispose();
            }
            catch (Exception)
            {
            }
            finally
            {

            }
        }

    }
}
