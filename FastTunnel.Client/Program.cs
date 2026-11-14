// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using FastTunnel.Core.Configuration;
using FastTunnel.Core.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Serilog;

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
            .ConfigureAppConfiguration((hostContext, config) =>
            {
                // 多格式配置：appsettings.json 由默认 builder 加载，这里补齐
                // .ini 变体（含环境变体），后加载覆盖先加载。
                var loaded = config.AddFastTunnelConfigFiles(
                    configDirectory: ".", environmentName: hostContext.HostingEnvironment.EnvironmentName,
                    includeJson: false);
                if (loaded.Count > 0)
                {
                    Log.Information("已加载扩展格式配置文件：{Files}", string.Join(", ", loaded));
                }
            })
            .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .WriteTo.Console())
            .ConfigureServices((hostContext, services) =>
            {
                // -------------------FastTunnel START------------------
                services.AddFastTunnelClient(hostContext.Configuration.GetSection("FastTunnel"));
                // -------------------FastTunnel EDN--------------------
            });
}
