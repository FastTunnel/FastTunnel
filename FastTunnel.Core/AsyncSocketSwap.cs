using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core
{
    public class AsyncSocketSwap
    {
        private Socket m_sockt1;
        private Socket m_sockt2;
        bool m_swaping = false;

        public AsyncSocketSwap(Socket sockt1, Socket sockt2)
        {
            m_sockt1 = sockt1;
            m_sockt2 = sockt2;
        }

        public AsyncSocketSwap BeforeSwap(Action fun)
        {
            if (m_swaping)
                throw new Exception("BeforeSwap must be invoked before StartSwap!");

            fun?.Invoke();
            return this;
        }

        private void StartSwap()
        {
            m_swaping = true;

            var rcv1 = new DataReciver(m_sockt1);
            rcv1.OnComplete += Rcv1_OnComplete;
            rcv1.ReciveOne();

            var rcv2 = new DataReciver(m_sockt2);
            rcv2.OnComplete += Rcv2_OnComplete;
            rcv2.ReciveOne();
        }

        public void StartSwapAsync()
        {
            Task.Run(() =>
            {
                try
                {
                    StartSwap();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
            });
        }

        private void Rcv1_OnComplete(DataReciver send, byte[] buffer, int index, int count)
        {
            m_sockt2.Send(buffer, index, count, SocketFlags.None);
            send.ReciveOne();
        }

        private void Rcv2_OnComplete(DataReciver send, byte[] buffer, int index, int count)
        {
            m_sockt1.Send(buffer, index, count, SocketFlags.None);
            send.ReciveOne();
        }
    }
}
