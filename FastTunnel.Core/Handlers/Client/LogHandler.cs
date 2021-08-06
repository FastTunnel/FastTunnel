using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Client;
using System.Threading.Tasks;
using System.Threading;

namespace FastTunnel.Core.Handlers.Client
{
    public class LogHandler : IClientHandler
    {
        ILogger<LogHandler> _logger;

        public LogHandler(ILogger<LogHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken)
        {
            _logger.LogInformation(msg.Replace("\n", string.Empty));
            await Task.CompletedTask;
        }
    }
}
