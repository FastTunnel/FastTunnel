using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Listener
{
    public class PortProxyListener
    {
        ILogger _logerr;

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        int m_numConnectedSockets;

        bool shutdown = false;
        ForwardDispatcher _requestDispatcher;
        Socket listenSocket;
        WebSocket client;

        public PortProxyListener(string ip, int port, ILogger logerr, WebSocket client)
        {
            this.client = client;
            _logerr = logerr;
            this.ListenIp = ip;
            this.ListenPort = port;

            IPAddress ipa = IPAddress.Parse(ListenIp);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);

            listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(localEndPoint);
        }

        public void Start(ForwardDispatcher requestDispatcher)
        {
            shutdown = false;
            _requestDispatcher = requestDispatcher;

            listenSocket.Listen();

            StartAccept(null);
        }

        private void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            _logerr.LogDebug($"【{ListenIp}:{ListenPort}】: StartAccept");
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(AcceptEventArg_Completed);
            }
            else
            {
                // socket must be cleared since the context object is being reused
                acceptEventArg.AcceptSocket = null;
            }

            bool willRaiseEvent = listenSocket.AcceptAsync(acceptEventArg);
            if (!willRaiseEvent)
            {
                ProcessAcceptAsync(acceptEventArg);
            }
        }

        private void ProcessAcceptAsync(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var accept = e.AcceptSocket;

                Interlocked.Increment(ref m_numConnectedSockets);

                _logerr.LogInformation($"【{ListenIp}:{ListenPort}】Accepted. There are {{0}} clients connected to the port",
                    m_numConnectedSockets);

                // 将此客户端交由Dispatcher进行管理
                _requestDispatcher.DispatchAsync(accept, client);

                // Accept the next connection request
                StartAccept(e);
            }
            else
            {
                Stop();
            }
        }

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAcceptAsync(e);
        }

        public void Stop()
        {
            if (shutdown)
                return;

            try
            {
                if (listenSocket.Connected)
                {
                    listenSocket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                shutdown = true;
                listenSocket.Close();
                Interlocked.Decrement(ref m_numConnectedSockets);
            }
        }

    }
}
