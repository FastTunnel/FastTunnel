using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FastTunnel.Core;
using FastTunnel.Core.Client;
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

namespace FastTunnel.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            LogManager.LoadConfiguration("Nlog.config");
            var logger = LogManager.GetCurrentClassLogger();
            logger.Debug("===== Sevice Start =====");

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
            client.Login();

            while (true)
            {
                Thread.Sleep(10000 * 60);
            }
        }

        private static void Config(ServiceCollection service)
        {
            service.AddTransient<FastTunnelClient>()
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
