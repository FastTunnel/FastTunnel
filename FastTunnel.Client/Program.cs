using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FastTunnel.Core;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using FastTunnel.Core.Host;
using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Models;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Logger;

namespace FastTunnel.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            LogManager.Configuration = NlogConfig.getNewConfig();
            var logger = LogManager.GetCurrentClassLogger();
            logger.Debug("===== FastTunnel Client Start =====");

            try
            {
                var servicesProvider = new Host().Config(Config).Build();

                Run(servicesProvider);

                while (true)
                {
                    Thread.Sleep(10000 * 60);
                }
            }
            catch (Exception ex)
            {
                // NLog: catch any exception and log it.
                logger.Error(ex, "Stopped program because of exception");
                throw;
            }
            finally
            {
                // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
                LogManager.Shutdown();
            }
        }

        private static void Run(IServiceProvider servicesProvider)
        {
            var client = servicesProvider.GetRequiredService<FastTunnelClient>();
            var config = servicesProvider.GetRequiredService<ClientConfig>();

            client.Login(() =>
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
                        SSH = config.SSH
                    }
                });

                return _client;
            }, config.Common);

            while (true)
            {
                Thread.Sleep(10000 * 60);
            }
        }

        private static void Config(ServiceCollection service)
        {
            service.AddSingleton<FastTunnelClient>()
                 .AddSingleton<ClientHeartHandler>()
                 .AddSingleton<LogHandler>()
                 .AddSingleton<NewCustomerHandler>()
                 .AddSingleton<NewSSHHandler>()
                 .AddSingleton<ClientConfig>(implementationFactory);
        }

        private static ClientConfig implementationFactory(IServiceProvider arg)
        {
            var conf = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .Build();

            var settings = conf.Get<Appsettings>();
            return settings.ClientSettings;
        }
    }
}
