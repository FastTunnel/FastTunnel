using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Server.Service
{
    public class ServiceFastTunnelServer : IHostedService
    {
        ILogger<ServiceFastTunnelServer> _logger;
        FastTunnelServer _fastTunnelServer;

        public ServiceFastTunnelServer(ILogger<ServiceFastTunnelServer> logger, IConfiguration configuration)
        {
            _logger = logger;
            _fastTunnelServer = new FastTunnelServer(_logger, configuration.Get<Appsettings>().ServerSettings);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Starting =====");

            try
            {
                _fastTunnelServer.Run();
            }
            catch (Exception ex)
            {
                // NLog: catch any exception and log it.
                _logger.LogError(ex, "Server Error");
                Console.WriteLine(ex);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Stoping =====");
            return Task.CompletedTask;
        }
    }
}
