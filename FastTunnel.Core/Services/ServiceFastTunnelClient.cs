using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.IO;

namespace FastTunnel.Core.Services
{
    public class ServiceFastTunnelClient : IHostedService
    {
        readonly ILogger<ServiceFastTunnelClient> _logger;
        readonly IFastTunnelClient _fastTunnelClient;

        public ServiceFastTunnelClient(ILogger<ServiceFastTunnelClient> logger, IFastTunnelClient fastTunnelClient)
        {
            _logger = logger;
            _fastTunnelClient = fastTunnelClient;

            //AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                _fastTunnelClient.StartAsync(cancellationToken);
            }, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Stoping =====");
            return Task.CompletedTask;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                _logger.LogError("【UnhandledException】" + e.ExceptionObject);
                var type = e.ExceptionObject.GetType();
                _logger.LogError("ExceptionObject GetType " + type);
            }
            catch
            {
            }
        }

        private void CurrentDomain_FirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is DirectoryNotFoundException)
            {
                // nlog第一次找不到文件的错误，跳过
            }
            else if (e.Exception is PlatformNotSupportedException)
            {
                // log4net
            }
            else if (e.Exception is IOException && e.Exception.Source == "System.Net.Security")
            {
            }
            else
            {
                _logger.LogError(e.Exception, "【FirstChanceException】");
            }
        }
    }
}
