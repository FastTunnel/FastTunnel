using FastTunnel.Core.Core;
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
        public static void AddFastTunnelServer(this IServiceCollection services)
        {
            services.AddTransient<IAuthenticationFilter, DefaultAuthenticationFilter>();

            services.AddHostedService<ServiceFastTunnelServer>();
        }

        public static void AddFastTunnelClient(this IServiceCollection services) {
            services.AddSingleton<FastTunnelClient>()
                .AddSingleton<ClientHeartHandler>()
                .AddSingleton<LogHandler>()
                .AddSingleton<HttpRequestHandler>()
                .AddSingleton<NewSSHHandler>();

            services.AddHostedService<ServiceFastTunnelClient>();
        }
    }
}
