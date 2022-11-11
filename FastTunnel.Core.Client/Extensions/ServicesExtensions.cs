// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FastTunnel.Core.Client.Extensions
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

            services.AddTransient<IFastTunnelClient, FastTunnelClient>()
                .AddSingleton<LogHandler>()
                .AddSingleton<SwapHandler>();

            services.AddHostedService<ServiceFastTunnelClient>();
        }

    }
}
