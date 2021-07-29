using FastTunnel.Core.Client;
using FastTunnel.Core.Config;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FastTunnel.Core.Extensions
{
    public static class ServicesExtensions
    {
        /// <summary>
        /// 添加服务端后台进程
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelServer(this IServiceCollection services, IConfigurationSection configurationSection)
        {
            services.Configure<DefaultServerConfig>(configurationSection);

            services.AddSingleton<IAuthenticationFilter, DefaultAuthenticationFilter>();
            services.AddSingleton<FastTunnelServer, FastTunnelServer>();

            services.AddHostedService<ServiceFastTunnelServer>();
        }

        /// <summary>
        /// 添加客户端后台进程
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelClient(this IServiceCollection services, IConfigurationSection configurationSection)
        {
            services.Configure<DefaultClientConfig>(configurationSection);

            services.AddSingleton<IFastTunnelClient, FastTunnelClient>()
                .AddSingleton<ClientHeartHandler>()
                .AddSingleton<LogHandler>()
                .AddSingleton<HttpRequestHandler>()
                .AddSingleton<NewSSHHandler>();

            services.AddHostedService<ServiceFastTunnelClient>();
        }
    }
}
