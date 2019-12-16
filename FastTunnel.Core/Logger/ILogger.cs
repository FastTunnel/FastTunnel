using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Logger
{
    public interface ILogger
    {
        void Error(string msg);

        void Warning(string msg);

        void Debug(string msg);

        void Info(string msg);
    }
}
