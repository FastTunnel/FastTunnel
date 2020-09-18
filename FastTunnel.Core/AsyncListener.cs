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

        Action<Socket> receiveClient;
        Socket listenSocket;

        bool Shutdown { get; set; }

        // Thread signal.  
        ManualResetEvent allDone = new ManualResetEvent(false);

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

        public void Listen(Action<Socket> receiveClient)
        {
            this.receiveClient = receiveClient;

            listenSocket.Listen(100);

            // post accepts on the listening socket
            StartAccept(null);
        }

        public void StartAccept(SocketAsyncEventArgs acceptEventArg)
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

        int m_numConnectedSockets;

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            Interlocked.Increment(ref m_numConnectedSockets);
            Console.WriteLine("Client connection accepted. There are {0} clients connected to the server",
                m_numConnectedSockets);

            var accept = e.AcceptSocket;

            // Accept the next connection request
            StartAccept(e);

            receiveClient.Invoke(accept);
        }

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        public void ShutdownAndClose()
        {
            Shutdown = true;
            try
            {
                listenSocket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex)
            {
            }
            finally
            {
                listenSocket.Close();
            }
        }
    }
}
