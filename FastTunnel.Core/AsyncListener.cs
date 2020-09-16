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
        Socket listener;

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

            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(localEndPoint);
        }

        public void Listen(Action<Socket> receiveClient)
        {
            this.receiveClient = receiveClient;

            Task.Run(() =>
            {
                try
                {
                    listener.Listen(100);

                    while (true)
                    {
                        // Set the event to nonsignaled state.  
                        allDone.Reset();

                        // Start an asynchronous socket to listen for connections.  
                        _logerr.LogDebug($"Waiting for a connection {listener.Handle}");
                        listener.BeginAccept(new AsyncCallback(AcceptCallback), listener);

                        // Wait until a connection is made before continuing.
                        allDone.WaitOne();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            });
        }

        void AcceptCallback(IAsyncResult ar)
        {
            if (Shutdown)
                return;

            // Signal the main thread to continue.  
            allDone.Set();

            // Get the socket that handles the client request.  
            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);

            receiveClient.Invoke(handler);
        }

        public void ShutdownAndClose()
        {
            Shutdown = true;
            try
            {
                listener.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex)
            {
            }
            finally
            {
                listener.Close();
            }
        }
    }
}
