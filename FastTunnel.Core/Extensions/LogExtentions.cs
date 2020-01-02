using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class LogExtentions
    {
        public static void LogError(this ILogger logger, object ex)
        {
            logger.LogError(ex.ToString());
        }
    }
}
