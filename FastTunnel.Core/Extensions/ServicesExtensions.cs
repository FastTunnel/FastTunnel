// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.MiddleWares;
using FastTunnel.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;
using Yarp.Sample;
using Microsoft.AspNetCore.Builder;
using FastTunnel.Core.Filters;
using Microsoft.AspNetCore.Mvc.Filters;
using FastTunnel.Core.Models;
using FastTunnel.Core.Handlers.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Http;

namespace FastTunnel.Core
{
    public static class ServicesExtensions
    {
        /// <summary>
        /// 客户端依赖及HostedService
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelClient(this IServiceCollection services, IConfigurationSection configurationSection)
        {
            services.Configure<DefaultClientConfig>(configurationSection);
            services.AddFastTunnelClient();
        }

        public static void AddFastTunnelClient(this IServiceCollection services)
        {
            services.AddTransient<IFastTunnelClient, FastTunnelClient>()
                .AddSingleton<IExceptionFilter, FastTunnelExceptionFilter>()
                .AddSingleton<LogHandler>()
                .AddSingleton<SwapHandler>();

            services.AddHostedService<ServiceFastTunnelClient>();
        }

        /// <summary>
        /// 添加服务端后台进程
        /// </summary>
        /// <param name="services"></param>
        public static void AddFastTunnelServer(this IServiceCollection services, IConfigurationSection configurationSection)
        {
            services.AddReverseProxy().LoadFromMemory();
            services.AddSingleton<IForwarderHttpClientFactory, FastTunnelForwarderHttpClientFactory>();

            services.Configure<DefaultServerConfig>(configurationSection)
                .AddSingleton<IExceptionFilter, FastTunnelExceptionFilter>()
                .AddTransient<ILoginHandler, LoginHandler>()
                .AddSingleton<FastTunnelClientHandler>()
                .AddSingleton<FastTunnelSwapHandler>()
                .AddSingleton<FastTunnelServer>();
        }

        /// <summary>
        /// 服务端中间件
        /// </summary>
        /// <param name="app"></param>
        public static void UseFastTunnelServer(this IApplicationBuilder app)
        {
            app.UseWebSockets();

            var swapHandler = app.ApplicationServices.GetRequiredService<FastTunnelSwapHandler>();
            var clientHandler = app.ApplicationServices.GetRequiredService<FastTunnelClientHandler>();
            app.Use(clientHandler.Handle);
            app.Use(swapHandler.Handle);
        }

        public static void MapFastTunnelServer(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapReverseProxy();
            endpoints.MapFallback(context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptionsMonitor<DefaultServerConfig>>();
                var host = context.Request.Host.Host;
                if (!host.EndsWith(options.CurrentValue.WebDomain) || host.Equals(options.CurrentValue.WebDomain))
                {
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                context.Response.StatusCode = 200;
                context.Response.WriteAsync(TunnelResource.Page_NotFound, CancellationToken.None);
                return Task.CompletedTask;
            });
        }
    }
}
