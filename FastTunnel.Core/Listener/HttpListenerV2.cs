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

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        int m_numConnectedSockets;

        public event OnClientChangeLine OnClientsChange;

        bool shutdown = false;
        IListenerDispatcher _requestDispatcher;
        Socket listenSocket;
        public IList<Socket> ConnectedSockets = new List<Socket>();

        Server.Server server;

        public HttpListenerV2(string ip, int port, ILogger logger)
        {
            _logger = logger;
            this.ListenIp = ip;
            this.ListenPort = port;

            server = new Server.Server(1000, 512);
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
            _logger.LogDebug($"【{ListenIp}:{ListenPort}】: StartAccept");
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

                _logger.LogInformation($"【{ListenIp}:{ListenPort}】Accepted. There are {{0}} clients connected to the port",
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
    }
}
