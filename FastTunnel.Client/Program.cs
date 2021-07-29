using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using FastTunnel.Core.Client;

namespace FastTunnel.Client
{
    class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
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
