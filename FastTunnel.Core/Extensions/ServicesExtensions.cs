using FastTunnel.Core.Client;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class ServicesExtensions
    {
        /// <summary>
        /// 添加服务端后台进程
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelServer(this IServiceCollection services)
        {
            services.AddSingleton<IFastTunnelAuthenticationFilter, DefaultAuthenticationFilter>();
            services.AddSingleton<FastTunnelServer, FastTunnelServer>();

            services.AddHostedService<ServiceFastTunnelServer>();
        }

        /// <summary>
        /// 添加客户端后台进程
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelClient(this IServiceCollection services)
        {
            services.AddSingleton<FastTunnelClient>()
                .AddSingleton<ClientHeartHandler>()
                .AddSingleton<LogHandler>()
                .AddSingleton<HttpRequestHandler>()
                .AddSingleton<NewSSHHandler>();

            services.AddHostedService<ServiceFastTunnelClient>();
        }
    }
}
