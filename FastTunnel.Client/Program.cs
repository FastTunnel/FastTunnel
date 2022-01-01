// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.AspNetCore.Builder;
using FastTunnel.Core;
using Microsoft.Extensions.Configuration;

namespace FastTunnel.Client;

class Program
{
    public static void Main(string[] args)
    {
        try
        {
            CreateHostBuilder(args).Build().Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((hostContext, services) =>
            {
                    // -------------------FastTunnel START------------------
                    services.AddFastTunnelClient(hostContext.Configuration.GetSection("ClientSettings"));
                    // -------------------FastTunnel EDN--------------------
                })
            .ConfigureLogging((HostBuilderContext context, ILoggingBuilder logging) =>
            {
                var enableFileLog = (bool)(context.Configuration.GetSection("EnableFileLog")?.Get(typeof(bool)) ?? false);
                if (enableFileLog)
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddLog4Net();
                }
            });
}
