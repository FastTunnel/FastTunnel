// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Serilog;
using FastTunnel.Core.Extensions;

namespace FastTunnel.Client;

class Program
{
    public static int Main(string[] args)
    {
        // The initial "bootstrap" logger is able to log errors during start-up. It's completely replaced by the
        // logger configured in `UseSerilog()` below, once configuration and dependency-injection have both been
        // set up successfully.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        Log.Information("Starting up!");

        try
        {
            CreateHostBuilder(args).Build().Run();

            Log.Information("Stopped cleanly");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "An unhandled exception occured during bootstrapping");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .UseSerilog((context, services, configuration) => configuration
                .MinimumLevel.Debug()
                .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day)
                .WriteTo.Console())
            .ConfigureServices((hostContext, services) =>
            {
                // -------------------FastTunnel START------------------
                services.AddFastTunnelClient(hostContext.Configuration.GetSection("ClientSettings"));
                // -------------------FastTunnel EDN--------------------
            });
}
