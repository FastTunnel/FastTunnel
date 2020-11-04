using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Handlers.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FastTunnel.Core.Listener
{
    public class PortProxyListener : IListener
    {
        ILogger _logerr;

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        int m_numConnectedSockets;

        public event OnClientChangeLine OnClientsChange;

        bool shutdown = false;
        IListenerDispatcher _requestDispatcher;
        Socket listenSocket;
        public IList<Socket> ConnectedSockets = new List<Socket>();

        public PortProxyListener(string ip, int port, ILogger logerr)
        {
            _logerr = logerr;
            this.ListenIp = ip;
            this.ListenPort = port;

            IPAddress ipa = IPAddress.Parse(ListenIp);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, ListenPort);

            listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(localEndPoint);
        }

        private void OnOffLine(Socket socket)
        {
            if (ConnectedSockets.Remove(socket))
                OnClientsChange?.Invoke(socket, ConnectedSockets.Count, true);
        }

        private void OnAccept(Socket socket)
        {
            ConnectedSockets.Add(socket);
            OnClientsChange?.Invoke(socket, ConnectedSockets.Count, false);
        }

        public void Start(IListenerDispatcher requestDispatcher, int backlog = 100)
        {
            shutdown = false;
            _requestDispatcher = requestDispatcher;

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
                Interlocked.Decrement(ref m_numConnectedSockets);
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

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var accept = e.AcceptSocket;
                OnAccept(accept);

                Interlocked.Increment(ref m_numConnectedSockets);

                _logerr.LogInformation($"【{ListenIp}:{ListenPort}】Accepted. There are {{0}} clients connected to the port",
                    m_numConnectedSockets);

                // Accept the next connection request
                StartAccept(e);

                // 将此客户端交由Dispatcher进行管理
                _requestDispatcher.Dispatch(accept, this.OnOffLine);
            }
            else
            {
                Stop();
            }
        }

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        public void Close()
        {
        }

        public void Start(int backlog = 100)
        {
            throw new NotImplementedException();
        }
    }
}
