using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Host;
using FastTunnel.Core.Logger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using System;
using System.IO;
using System.Threading;

namespace SuiDao.Server
{
    public class Program
    {
        static Appsettings appsettings;

        static void Main(string[] args)
        {
            LogManager.Configuration = NlogConfig.getNewConfig();
            var logger = LogManager.GetCurrentClassLogger();
            logger.Debug("===== FastTunnel Server Start =====");

            try
            {
                var servicesProvider = new Host().Config(Config).Build();
                Run(servicesProvider);
            }
            catch (Exception ex)
            {
                // NLog: catch any exception and log it.
                logger.Error(ex);
                Console.WriteLine(ex);
            }
            finally
            {
                // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
                LogManager.Shutdown();
            }
        }

        private static ServerConfig implementationFactory(IServiceProvider arg)
        {
            if (appsettings == null)
            {
                var conf = new ConfigurationBuilder()
                  .SetBasePath(Directory.GetCurrentDirectory())
                  .AddJsonFile("appsettings.json", true, true)
                  .Build();

                appsettings = conf.Get<Appsettings>();
            }

            return appsettings.ServerSettings;
        }

        private static void Config(ServiceCollection service)
        {
            service.AddSingleton<FastTunnelServer>()
                .AddSingleton<ServerConfig>(implementationFactory)
                .AddSingleton<LoginHandler>() 
                .AddSingleton<SwapMsgHandler>()
                .AddSingleton<HeartHandler>()
                .AddSingleton<IConfigHandler, SuiDaoConfigHandler>();
        }

        private static void Run(IServiceProvider servicesProvider)
        {
            var server = servicesProvider.GetRequiredService<FastTunnelServer>();
            server.Run();

            while (true)
            {
                Thread.Sleep(10000 * 60);
            }
        }
    }
}
