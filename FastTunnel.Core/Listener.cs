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
        private string _ip;
        private int _port;
        Action<Socket, T> handler;
        Socket socket;
        T _data;

        public Listener(string ip, int port, Action<Socket, T> acceptCustomerHandler, T data)
        {
            _data = data;
            this._ip = ip;
            this._port = port;
            handler = acceptCustomerHandler;

            IPAddress ipa = IPAddress.Parse(_ip);
            IPEndPoint ipe = new IPEndPoint(ipa, _port);

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(ipe);
        }

        public void Listen()
        {
            socket.Listen(100);
            ThreadPool.QueueUserWorkItem((state) =>
            {
                var _socket = state as Socket;
                while (true)
                {
                    Socket client = _socket.Accept();
                    string point = client.RemoteEndPoint.ToString();
                    Console.WriteLine($"收到请求 {point}");

                    ThreadPool.QueueUserWorkItem(ReceiveCustomer, client);
                }

            }, socket);
        }

        private void ReceiveCustomer(object state)
        {
            var client = state as Socket;
            handler.Invoke(client, _data);
        }
    }
}
