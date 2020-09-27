using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core
{
    public delegate void OnCompleteHandler(DataReciver send, byte[] buffer, int index, int count);
    public delegate void OnError(DataReciver send, SocketAsyncEventArgs e);

    public class DataReciver
    {
        private Socket m_client;

        public event OnCompleteHandler OnComplete;
        public event OnError OnError;

        byte[] buffer = new byte[1024 * 1024];
        SocketAsyncEventArgs rcv_event;

        public Socket Socket => m_client;

        public DataReciver(Socket client)
        {
            this.m_client = client;

            rcv_event = new SocketAsyncEventArgs();
            rcv_event.Completed += Rcv_event_Completed;
            rcv_event.SetBuffer(buffer);
        }

        public void ReciveOne()
        {
            var willRaise = m_client.ReceiveAsync(rcv_event);
            if (!willRaise)
            {
                Process(rcv_event);
            }
        }

        private void Rcv_event_Completed(object sender, SocketAsyncEventArgs e)
        {
            Process(e);
        }

        private void Process(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                if (e.BytesTransferred == 0)
                {

                }
                else
                {
                    OnComplete?.Invoke(this, buffer, e.Offset, e.BytesTransferred);
                }
            }
            else
            {
                OnError?.Invoke(this, e);
            }
        }
    }
}
