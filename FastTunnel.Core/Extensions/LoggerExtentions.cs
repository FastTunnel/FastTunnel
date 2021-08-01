using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class LoggerExtentions
    {
        public static void LogError(this ILogger logger, Exception ex)
        {
            logger.LogError(ex, string.Empty);
        }
    }
}
