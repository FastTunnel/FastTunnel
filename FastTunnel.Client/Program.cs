using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Extensions;

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
                    logging.AddLog4Net();
                });
    }
}
