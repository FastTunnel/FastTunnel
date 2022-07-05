// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Extensions;
using FastTunnel.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

[assembly: HostingStartup(typeof(FastTunnelHostingStartup))]

namespace FastTunnel.Hosting;

public class FastTunnelHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices((webHostBuilderContext, services) =>
        {
            services.AddFastTunnelServer(webHostBuilderContext.Configuration.GetSection("FastTunnel"));
        });

        builder.UseKestrel((context, options) =>
        {
            var basePort = context.Configuration.GetValue<int?>("FastTunnel:BinPort") ?? 1270;
            options.ListenAnyIP(basePort, listenOptions =>
            {
                listenOptions.UseConnectionFastTunnel();
            });
        });

        //builder.Configure((webHostBuilderContext, app) =>
        //{
        //    app.UseFastTunnelServer();
        //});
    }
}
