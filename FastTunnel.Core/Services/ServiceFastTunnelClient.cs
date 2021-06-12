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
        readonly ILogger<ServiceFastTunnelClient> _logger;
        readonly FastTunnelClient _fastTunnelClient;
        readonly ClientConfig _config;

        public ServiceFastTunnelClient(
            ILogger<ServiceFastTunnelClient> logger,
            IConfiguration configuration,
            FastTunnelClient fastTunnelClient)
        {
            _logger = logger;
            _fastTunnelClient = fastTunnelClient;
            _config = configuration.Get<AppSettings>().ClientSettings;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                _fastTunnelClient.Start(() =>
                {
                    Connecter _client;

                    try
                    {
                        // 连接到的目标IP
                        _client = new Connecter(_config.Common.ServerAddr, _config.Common.ServerPort);
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
                            Webs = _config.Webs,
                            SSH = _config.SSH,
                            AuthInfo = "ODadoNDONODHSoDMFMsdpapdoNDSHDoadpwPDNoWAHDoNfa"
                        },
                    });

                    return _client;
                }, _config.Common);


            }, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Stoping =====");
            return Task.CompletedTask;
        }
    }
}
