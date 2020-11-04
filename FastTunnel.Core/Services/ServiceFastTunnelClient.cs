using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Services
{
    public class ServiceFastTunnelClient : IHostedService
    {
        ILogger<ServiceFastTunnelClient> _logger;
        IConfiguration _configuration;
        FastTunnelClient _fastTunnelClient;
        ClientConfig config;

        public ServiceFastTunnelClient(
            ILogger<ServiceFastTunnelClient> logger,
            IConfiguration configuration,
            FastTunnelClient fastTunnelClient)
        {
            _logger = logger;
            _configuration = configuration;
            _fastTunnelClient = fastTunnelClient;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Start =====");

            config = _configuration.Get<AppSettings>().ClientSettings;

            _fastTunnelClient.Login(() =>
            {
                Connecter _client;

                try
                {
                    // 连接到的目标IP
                    _client = new Connecter(config.Common.ServerAddr, config.Common.ServerPort);
                    _client.Connect();
                }
                catch (Exception)
                {
                    Thread.Sleep(5000);
                    throw;
                }

                // 登录
                _client.Send(new Message<LogInMassage>
                {
                    MessageType = MessageType.C_LogIn,
                    Content = new LogInMassage
                    {
                        Webs = config.Webs,
                        SSH = config.SSH,
                        AuthInfo = "ODadoNDONODHSoDMFMsdpapdoNDSHDoadpwPDNoWAHDoNfa"
                    },
                });

                return _client;
            }, config.Common);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Stoping =====");
            return Task.CompletedTask;
        }
    }
}
