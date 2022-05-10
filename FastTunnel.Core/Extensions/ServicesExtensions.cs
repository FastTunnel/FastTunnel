// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FastTunnel.Core.Extensions;

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
        services.Configure<DefaultServerConfig>(configurationSection)
            .AddTransient<ILoginHandler, LoginHandler>()
            .AddSingleton<FastTunnelClientHandler>()
            .AddSingleton<FastTunnelServer>();
    }

    /// <summary>
    /// 服务端中间件
    /// </summary>
    /// <param name="app"></param>
    public static void UseFastTunnelServer(this IApplicationBuilder app)
    {
        app.UseWebSockets();

        // var swapHandler = app.ApplicationServices.GetRequiredService<FastTunnelSwapHandler>();
        var clientHandler = app.ApplicationServices.GetRequiredService<FastTunnelClientHandler>();
        app.Use(clientHandler.Handle);
    }
}
