using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Exceptions
{
    public class APIErrorException : Exception
    {
        public APIErrorException(string message)
            : base(message)
        {
        }
    }
}
