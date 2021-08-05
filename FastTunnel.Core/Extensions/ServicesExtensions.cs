using FastTunnel.Core.Client;
using FastTunnel.Core.Config;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.MiddleWares;
using FastTunnel.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;
using Yarp.Sample;
using Microsoft.AspNetCore.Builder;

namespace FastTunnel.Core
{
    public static class ServicesExtensions
    {
        /// <summary>
        /// 添加服务端后台进程
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelServer(this IServiceCollection services, IConfigurationSection configurationSection)
        {
            services.AddReverseProxy().LoadFromMemory();

            services.Configure<DefaultServerConfig>(configurationSection);

            services.AddSingleton<IAuthenticationFilter, DefaultAuthenticationFilter>();
            services.AddSingleton<FastTunnelServer, FastTunnelServer>();

            services.AddSingleton<IForwarderHttpClientFactory, FastTunnelForwarderHttpClientFactory>();
            services.AddSingleton<FastTunnelClientHandler, FastTunnelClientHandler>();
            services.AddSingleton<FastTunnelSwapHandler, FastTunnelSwapHandler>();
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
                .AddSingleton<SwapHandler>()
                .AddSingleton<SwapHandler>();

            services.AddHostedService<ServiceFastTunnelClient>();
        }

        public static void UseFastTunnel(this IApplicationBuilder app)
        {
            app.UseWebSockets();
            var swapHandler = app.ApplicationServices.GetRequiredService<FastTunnelSwapHandler>();
            var clientHandler = app.ApplicationServices.GetRequiredService<FastTunnelClientHandler>();
            app.Use(clientHandler.Handle);
            app.Use(swapHandler.Handle);
        }
    }
}