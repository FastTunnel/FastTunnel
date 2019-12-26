using FastTunnel.Core.Logger;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastTunnel.Core
{
    public class Listener<T>
    {
        ILogger _logerr;

        public string IP { get; set; }

        public int Port { get; set; }

        Action<Socket, T> handler;
        Socket ls;
        T _data;


        public Listener(string ip, int port, ILogger logerr, Action<Socket, T> acceptCustomerHandler, T data)
        {
            _logerr = logerr;
            _data = data;
            this.IP = ip;
            this.Port = port;
            handler = acceptCustomerHandler;

            IPAddress ipa = IPAddress.Parse(IP);
            IPEndPoint ipe = new IPEndPoint(ipa, Port);

            ls = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ls.Bind(ipe);
        }

        public void Listen()
        {
            ls.Listen(100);
            ThreadPool.QueueUserWorkItem((state) =>
            {
                var _socket = state as Socket;

                while (true)
                {
                    try
                    {
                        var client = _socket.Accept();

                        string point = client.RemoteEndPoint.ToString();
                        ThreadPool.QueueUserWorkItem(ReceiveCustomer, client);
                    }
                    catch (SocketException ex)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logerr.Error(ex);
                        throw;
                    }
                }

            }, ls);
        }

        private void ReceiveCustomer(object state)
        {
            var client = state as Socket;
            handler.Invoke(client, _data);
        }

        public void ShutdownAndClose()
        {
            try
            {
                if (ls.Connected)
                {
                    ls.Shutdown(SocketShutdown.Both);
                }
            }
            finally
            {
                ls.Close();
                _logerr.Debug("Listener closed");
            }
        }
    }
}
