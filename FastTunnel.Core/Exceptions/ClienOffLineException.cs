using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Exceptions
{
    public class ClienOffLineException : Exception
    {
        public ClienOffLineException(string message)
            : base(message)
        {
        }
    }
}
