using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using FastTunnel.Core.Services;
using Microsoft.Extensions.Hosting;
using FastTunnel.Core.Client;
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Client;
using Microsoft.Extensions.Logging;
using NLog.Web;
using FastTunnel.Core.Extensions;

namespace FastTunnel.Client
{
    class Program
    {
        public static void Main(string[] args)
        {
            var logger = NLogBuilder.ConfigureNLog("nlog.config").GetCurrentClassLogger();
            try
            {
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception exception)
            {
                //NLog: catch setup errors
                logger.Error(exception, "Stopped program because of exception");
                throw;
            }
            finally
            {
                // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
                NLog.LogManager.Shutdown();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    // -------------------FastTunnel START------------------
                    services.AddFastTunnelClient();
                    // -------------------FastTunnel EDN--------------------
                })
                .ConfigureLogging((HostBuilderContext context, ILoggingBuilder logging) =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                })
                .UseNLog();  // NLog: Setup NLog for Dependency injection
    }
}
