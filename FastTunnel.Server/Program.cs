using Microsoft.Extensions.Configuration;
using FastTunnel.Core;
using FastTunnel.Core.Logger;
using FastTunnel.Core.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NLog.Extensions.Logging;
using Microsoft.Extensions.Logging;
using NLog;
using FastTunnel.Core.Config;
using FastTunnel.Core.Host;

namespace FastTunnel.Server
{
    class Program
    {
        static Appsettings appsettings;

        static void Main(string[] args)
        {
            Console.WriteLine("Server Start!");
            var logger = LogManager.GetCurrentClassLogger();

            try
            {
                var servicesProvider = new Host().Config(Config).Build();
                Run(servicesProvider);
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
            service.AddTransient<FastTunnelServer>()
                .AddSingleton<ServerConfig>(implementationFactory);
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
