using FastTunnel.Core.Handlers.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core
{
    public class AsyncListener : IListener
    {
        ILogger _logerr;

        public string IP { get; set; }

        public int Port { get; set; }

        int m_numConnectedSockets;

        bool shutdown = false;
        IListenerDispatcher _requestDispatcher;
        Socket listenSocket;

        public AsyncListener(string ip, int port, ILogger logerr)
        {
            _logerr = logerr;
            this.IP = ip;
            this.Port = port;

            IPAddress ipa = IPAddress.Parse(IP);
            IPEndPoint localEndPoint = new IPEndPoint(ipa, Port);

            listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(localEndPoint);
        }

        public void Listen(IListenerDispatcher requestDispatcher)
        {
            shutdown = false;
            _requestDispatcher = requestDispatcher;

            listenSocket.Listen(100);

            StartAccept(null);
        }

        public void ShutdownAndClose()
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

                Interlocked.Increment(ref m_numConnectedSockets);
                _logerr.LogInformation($"【{IP}:{Port}】Accepted. There are {{0}} clients connected to the port",
                    m_numConnectedSockets);

                // Accept the next connection request
                StartAccept(e);

                // 将此客户端交由Dispatcher进行管理
                _requestDispatcher.Dispatch(accept);
            }
            else
            {
                ShutdownAndClose();
            }
        }

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }
    }
}
