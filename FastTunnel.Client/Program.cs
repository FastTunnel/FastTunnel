using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using FastTunnel.Core.Client;
using System;

namespace FastTunnel.Client
{
    class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .ConfigureServices((hostContext, services) =>
                {
                    // -------------------FastTunnel START------------------
                    services.AddFastTunnelClient(hostContext.Configuration.GetSection("ClientSettings"));

                    services.AddSingleton<IFastTunnelClient, FastTunnelClient>();
                    // -------------------FastTunnel EDN--------------------
                })
                .ConfigureLogging((HostBuilderContext context, ILoggingBuilder logging) =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddLog4Net();
                });
    }
}
