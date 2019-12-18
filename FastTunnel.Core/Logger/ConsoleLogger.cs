using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Logger
{
    public class ConsoleLogger : ILogger
    {
        public void Error(object msg)
        {
            Console.WriteLine(string.Format("Erro - {0}", msg?.ToString()));
        }

        public void Debug(string msg)
        {
            Console.WriteLine(string.Format("Debu - {0}", msg));
        }

        public void Error(string msg)
        {
            Console.WriteLine(string.Format("Erro - {0}", msg));
        }

        public void Info(string msg)
        {
            Console.WriteLine(string.Format("Info - {0}", msg));
        }

        public void Warning(string msg)
        {
            Console.WriteLine(string.Format("Warn - {0}", msg));
        }
    }
}
