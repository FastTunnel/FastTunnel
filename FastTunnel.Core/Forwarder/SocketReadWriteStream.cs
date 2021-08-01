using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder
{
    public class SocketReadWriteStream : IReadWriteStream
    {
        Socket socket;
        public SocketReadWriteStream(Socket socket)
        {
            this.socket = socket;
        }

        public int Read(byte[] buffer)
        {
            return socket.Receive(buffer);
        }

        public void Write(byte[] buffer, int index, int num)
        {
            socket.Send(buffer, index, num, SocketFlags.None);
        }
    }
}
