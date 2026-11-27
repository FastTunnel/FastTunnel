// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Globalization;
using FastTunnel.Core.Configuration;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.P2P;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace FastTunnel.Client;

class Program
{
    private const string DefaultStun = "stun:stun.cloudflare.com:3478";

    public static int Main(string[] args)
    {
        if (args is ["p2p-pair", ..])
            return RunManualPair(args);
        if (args is ["--help"] or ["-h"])
        {
            PrintPairHelp();
            return 0;
        }

        // The initial "bootstrap" logger is able to log errors during start-up. It's completely replaced by the
        // logger configured in `UseSerilog()` below, once configuration and dependency-injection have both been
        // set up successfully.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        Log.Information("Starting up!");
        if (args.Length == 0 && Environment.UserInteractive && !Console.IsInputRedirected)
            Log.Information("没有 FastTunnel 服务端时，可用 p2p-pair 打开手动配对向导。");

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

    private static int RunManualPair(string[] args)
    {
        if (args is ["p2p-pair", "--help" or "-h"])
        {
            PrintPairHelp();
            return 0;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try
        {
            bool visitor;
            int port;
            string? stun;
            if (args.Length == 1)
            {
                (visitor, port, stun) = ReadPairSettingsAsync(stop.Token).GetAwaiter().GetResult();
            }
            else if (args.Length is 3 or 4 && args[1] is ("share" or "connect") && TryPort(args[2], out port))
            {
                visitor = args[1] == "connect";
                stun = args.Length == 4 ? args[3] : null;
            }
            else
            {
                Console.Error.WriteLine("无法识别配对命令，请检查角色和端口。");
                PrintPairHelp();
                return 2;
            }

            PeerManualPair.RunAsync(visitor, port, stun, Console.In, Console.Out, stop.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"P2P 手动配对结束: {ex.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= Cancel; }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        }
    }

    private static void PrintPairHelp()
    {
        Console.WriteLine("FastTunnel P2P 手动配对，无需 FastTunnel 服务端。");
        Console.WriteLine("p2p-pair                                    打开交互向导");
        Console.WriteLine("p2p-pair share <服务端口> [stun:主机:端口]     提供端生成邀请");
        Console.WriteLine("p2p-pair connect <监听端口> [stun:主机:端口]  访客粘贴邀请并生成应答");
        Console.WriteLine("远程桌面示例：提供端端口 3389，访客监听 13389。");
    }

    private static async Task<(bool Visitor, int Port, string? Stun)> ReadPairSettingsAsync(CancellationToken token)
    {
        Console.WriteLine("FastTunnel 手动配对");
        Console.WriteLine("两端都运行 p2p-pair，各选一种角色。配对信息通过聊天互相复制。");
        Console.WriteLine("1  提供端，共享这台电脑上的服务");
        Console.WriteLine("2  访客端，访问另一台电脑上的服务");
        string role;
        while (true)
        {
            role = await ReadAsync("请选择 1 或 2：", token);
            if (role is "1" or "2")
                break;
            Console.WriteLine("请输入 1 或 2。");
        }

        var visitor = role == "2";
        var defaultPort = visitor ? 13389 : 3389;
        var description = visitor ? "本机监听端口" : "此电脑上的服务端口";
        int port;
        while (true)
        {
            var value = await ReadAsync($"{description} [{defaultPort}]，回车用括号内的远程桌面示例端口：", token);
            if (value.Length == 0)
            {
                port = defaultPort;
                break;
            }
            if (TryPort(value, out port))
                break;
            Console.WriteLine("端口应在 1–65535 之间。");
        }

        while (true)
        {
            var value = await ReadAsync($"STUN 地址 [{DefaultStun}]，回车使用公共节点，纯局域网输入 - 跳过：", token);
            if (value.Length == 0)
                return (visitor, port, DefaultStun);
            if (value == "-")
                return (visitor, port, null);
            var address = value.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) ? value : "stun:" + value;
            if (PeerManualPair.IsStunAddress(address))
                return (visitor, port, address);
            Console.WriteLine("地址格式无效，例如 stun:stun.cloudflare.com:3478。");
        }
    }

    private static async Task<string> ReadAsync(string prompt, CancellationToken token)
    {
        Console.WriteLine(prompt);
        var line = await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(token);
        return line?.Trim() ?? throw new IOException("输入已关闭，请在终端运行 p2p-pair。");
    }

    private static bool TryPort(string value, out int port)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

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
            .UseSerilog((context, configuration) =>
                ConfigureSerilog(configuration, context.Configuration))
            .ConfigureServices((hostContext, services) =>
            {
                // -------------------FastTunnel START------------------
                services.AddFastTunnelClient(hostContext.Configuration.GetSection("FastTunnel"));
                // -------------------FastTunnel EDN--------------------
            });

    /// <summary>
    /// AOT 安全的 Serilog 配置：不依赖 ReadFrom.Configuration 的反射式 sink 发现。
    /// 直接从 IConfiguration 读取级别与 sink 参数，代码静态调用，NativeAOT 下无反射裁剪风险。
    /// 与 Server/Program.cs 中的同名方法保持一致。
    /// </summary>
    private static LoggerConfiguration ConfigureSerilog(
        LoggerConfiguration logger, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var section = config.GetSection("Serilog");

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
