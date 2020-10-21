using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Global;
using FastTunnel.Server.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
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

            AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                _logger.LogError("【UnhandledException】" + e.ExceptionObject);
                _logger.LogError("【UnhandledException】" + JsonConvert.SerializeObject(e.ExceptionObject));
                var type = e.ExceptionObject.GetType();
                _logger.LogError("ExceptionObject GetType " + type);
            }
            catch
            {
            }
        }

        private void CurrentDomain_FirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is System.IO.DirectoryNotFoundException)
            {
                // nlog第一次找不到文件的错误，跳过
            }
            else
            {
                _logger.LogError(e.Exception, "【FirstChanceException】");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Server Starting =====");

            _fastTunnelServer = new FastTunnelServer(_logger, _configuration.Get<Appsettings>().ServerSettings);
            FastTunnelGlobal.AddFilter(_testAuthenticationFilter);

            try
            {
                _fastTunnelServer.Run();

                _logger.LogDebug("Server Run Success");
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
