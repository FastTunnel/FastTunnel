using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Global;
using FastTunnel.Server.Filters;
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
        TestAuthenticationFilter _testAuthenticationFilter;
        IConfiguration _configuration; 
        public ServiceFastTunnelServer(
            ILogger<ServiceFastTunnelServer> logger,
            IConfiguration configuration, 
            TestAuthenticationFilter testAuthenticationFilter)
        {
            _configuration = configuration;
            _testAuthenticationFilter = testAuthenticationFilter;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Starting =====");

            _fastTunnelServer = new FastTunnelServer(_logger, _configuration.Get<Appsettings>().ServerSettings);
            FastTunnelGlobal.AddFilter(_testAuthenticationFilter);

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
