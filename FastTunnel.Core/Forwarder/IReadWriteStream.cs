using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder
{
    public interface IReadWriteStream
    {
        int Read(byte[] buffer);

        void Write(byte[] buffer, int index, int num);
    }
}
