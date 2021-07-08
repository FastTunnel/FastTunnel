using FastTunnel.Core.Dispatchers;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets
{
    /// <summary>
    /// 异步数据交换
    /// 存在大文件下载时，导致栈溢出的问题
    /// </summary>
    public class AsyncSocketSwap : ISocketSwap
    {
        private Socket m_sockt1;
        private Socket m_sockt2;
        bool m_swaping = false;

        byte[] m_buffer = new byte[1024];
        SocketAsyncEventArgs e1;
        SocketAsyncEventArgs e2;

        public AsyncSocketSwap(Socket sockt1, Socket sockt2)
        {
            m_sockt1 = sockt1;
            m_sockt2 = sockt2;

            e1 = new SocketAsyncEventArgs();
            e2 = new SocketAsyncEventArgs();

            e1.Completed += IO_Completed;
            e2.Completed += IO_Completed;
            e1.UserToken = new SwapUserToken { Reciver = m_sockt1, Sender = m_sockt2 };
            e2.UserToken = new SwapUserToken { Reciver = m_sockt2, Sender = m_sockt1 };

            e1.SetBuffer(m_buffer, 0, 512);
            e2.SetBuffer(m_buffer, 512, 512);
        }

        public ISocketSwap BeforeSwap(Action fun)
        {
            if (m_swaping)
                throw new Exception("BeforeSwap must be invoked before StartSwap!");

            fun?.Invoke();
            return this;
        }

        public void StartSwap()
        {
            try
            {
                Console.WriteLine("StartSwapAsync");
                m_swaping = true;

                if (!m_sockt1.ReceiveAsync(e1))
                {
                    ProcessReceive(e1);
                }

                if (!m_sockt2.ReceiveAsync(e2))
                {
                    ProcessReceive(e2);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }

        private void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            var token = e.UserToken as SwapUserToken;
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                e.SetBuffer(e.Offset, 512);
                if (!token.Sender.SendAsync(e))
                {
                    ProcessSend(e);
                }
            }
            else
            {
                // close
                CloseSocket(token.Reciver);
            }
        }

        private void ProcessSend(SocketAsyncEventArgs e)
        {
            var token = e.UserToken as SwapUserToken;
            if (e.SocketError == SocketError.Success)
            {
                Console.WriteLine("ProcessSend:" + e.BytesTransferred);

                if (!token.Reciver.ReceiveAsync(e))
                {
                    e.SetBuffer(e.Offset, 512);
                    ProcessReceive(e);
                }
            }
            else
            {
                CloseSocket(token.Sender);
            }
        }

        private void CloseSocket(Socket socket)
        {
            try
            {
                Console.WriteLine("CloseSocket");

                try
                {
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch (Exception) { }
                socket.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public class SwapUserToken
        {
            public Socket Reciver { get; set; }

            public Socket Sender { get; set; }
        }
    }
}
