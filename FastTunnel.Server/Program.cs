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
        if (args is ["p2p-invite-key"])
        {
            Console.WriteLine(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            return 0;
        }

        // The initial "bootstrap" logger is able to log errors during start-up. It's completely replaced by the
        // logger configured in `UseSerilog()` below, once configuration and dependency-injection have both been
        // set up successfully.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        Log.Information("Starting up!");

        try
        {
            if (args is ["p2p-invite", ..])
                return PeerInviteCommand.Run(args);
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
            .UseSerilog((context, services, configuration) =>
                ConfigureSerilog(configuration, context.Configuration))
            .ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder.UseKestrel();

                // NativeAOT 不支持按程序集名字符串动态加载 IHostingStartup。
                // 显式注册原本由 FastTunnel.Api / FastTunnel.Hosting 的 HostingStartup 提供的服务。
                webHostBuilder.UseFastTunnelAotServices();

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

    /// <summary>
    /// AOT 安全的 Serilog 配置：Serilog.Settings.Configuration 的 ReadFrom.Configuration
    /// 靠 "Using" 里的程序集名反射发现 sink，NativeAOT 下反射目标被裁剪会抛异常或静默丢日志。
    /// 这里直接从 IConfiguration 读取级别与 sink 参数，用代码静态调用对应扩展，全链路无反射。
    /// </summary>
    private static LoggerConfiguration ConfigureSerilog(
        LoggerConfiguration logger, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var section = config.GetSection("Serilog");

        // MinimumLevel:Default / Override
        var defaultLevel = ParseLevel(section["MinimumLevel:Default"], Serilog.Events.LogEventLevel.Information);
        logger.MinimumLevel.Is(defaultLevel!.Value);
        foreach (var item in section.GetSection("MinimumLevel:Override").GetChildren())
        {
            var level = ParseLevel(item.Value, null);
            if (level.HasValue)
            {
                logger.MinimumLevel.Override(item.Key, level.Value);
            }
        }

        logger.Enrich.FromLogContext();
        logger.WriteTo.Console();

        // File sink：appsettings 里 WriteTo 段的 File 项当前是注释掉的（默认不写文件）。
        // 若配置里出现 Name=File，则按参数静态接入，保持与 JIT 版一致的可配置性。
        foreach (var sink in section.GetSection("WriteTo").GetChildren())
        {
            if (!string.Equals(sink["Name"], "File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = sink["Args:path"] ?? "Logs/log-.log";
            var rolling = ParseRolling(sink["Args:rollingInterval"]);
            logger.WriteTo.File(path, rollingInterval: rolling);
        }

        return logger;
    }

    private static Serilog.Events.LogEventLevel? ParseLevel(string? value, Serilog.Events.LogEventLevel? fallback) =>
        Enum.TryParse<Serilog.Events.LogEventLevel>(value, ignoreCase: true, out var level) ? level : fallback;

    private static Serilog.RollingInterval ParseRolling(string? value) =>
        Enum.TryParse<Serilog.RollingInterval>(value, ignoreCase: true, out var interval)
            ? interval
            : Serilog.RollingInterval.Day;
}
