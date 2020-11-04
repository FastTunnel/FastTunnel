using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FastTunnel.Core.Listener
{
    public class ClientListener : IListener
    {
        ILogger _logerr;

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        public event OnClientChangeLine OnClientsChange;

        bool shutdown = false;
        //IListenerDispatcher _requestDispatcher;
        Socket listenSocket;
        public IList<ClientConnection> ConnectedSockets = new List<ClientConnection>();
        FastTunnelServer _fastTunnelServer;

        public ClientListener(FastTunnelServer fastTunnelServer, string ip, int port, ILogger logerr)
        {
            _fastTunnelServer = fastTunnelServer;
            _logerr = logerr;
            this.ListenIp = ip;
            this.ListenPort = port;

            IPAddress ipa = IPAddress.Parse(ListenIp);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);

            listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(localEndPoint);
        }

        private void HandleNewClient(Socket socket)
        {
            // 此时的客户端可能有两种 1.登录的客户端 2.交换请求的客户端
            var client = new ClientConnection(_fastTunnelServer, socket, _logerr);
            ConnectedSockets.Add(client);

            // 接收客户端消息
            client.StartRecive();
        }

        public void Start(IListenerDispatcher requestDispatcher, int backlog = 100)
        {
            shutdown = false;
            // _requestDispatcher = requestDispatcher;

            listenSocket.Listen(backlog);

            StartAccept(null);
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
            }
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
                ProcessAccept(acceptEventArg);
            }
        }

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var accept = e.AcceptSocket;

                StartAccept(e);
                HandleNewClient(accept);
            }
            else
            {
                _logerr.LogError($"监听客户端异常 this={this.ToJson()} e={e.ToJson()}");
                Stop();
            }
        }

        public void Close()
        {
        }

        public void Start(int backlog = 100)
        {
            Start(null, backlog);
        }
    }
}
