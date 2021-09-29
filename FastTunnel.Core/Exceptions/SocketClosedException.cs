using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Exceptions
{
    public class SocketClosedException : Exception
    {
        public SocketClosedException(string msg) : base(msg)
        {
        }
    }
}
