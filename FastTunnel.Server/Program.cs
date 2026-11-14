// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Configuration;
using Serilog;

namespace FastTunnel.Server;

public class Program
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
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console())
            .ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder.UseKestrel();
                webHostBuilder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, "FastTunnel.Api;FastTunnel.Hosting");

                webHostBuilder.ConfigureAppConfiguration((hostingContext, config) =>
                {
                    // 多格式配置：config/ 下 appsettings.json / appsettings.ini 全部按序加载，
                    // 后加载覆盖先加载，环境变体最后追加；所有文件支持热重载。
                    var env = hostingContext.HostingEnvironment;
                    var loaded = config.AddFastTunnelConfigFiles("config", env.EnvironmentName);
                    if (loaded.Count > 0)
                    {
                        Log.Information("已加载配置文件：{Files}", string.Join(", ", loaded));
                    }
                });

                webHostBuilder.UseStartup<Startup>();
            });
}
