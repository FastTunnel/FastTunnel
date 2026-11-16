// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastTunnel.LoadRunner;

/// <summary>
/// 内置静态源站（Kestrel，keep-alive + Content-Length + 固定 1KB 页面）。
///
/// 用途：
/// 1. 没有 IIS 管理员权限时，作为等价源站验证整条链路（IIS 就绪后自动改用 IIS）；
/// 2. 作为「不经隧道直连源站」的基线。
///
/// 用 Kestrel 而不是手写 thread-per-connection：隧道两端在高压下会频繁建连/断连，
/// 线程模型撑不住时会把「源站扛不住」误判成「隧道不行」。
/// </summary>
internal static class OriginServer
{
    public static int Run(string[] args)
    {
        var port = 18080;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length)
            {
                port = int.Parse(args[++i]);
            }
        }

        var body = Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>origin</title></head>" +
            "<body><h1>benchmark target</h1>" +
            "<p>This page is served by the local origin and reaches the load generator through HTTP tunneling.</p>" +
            "</body></html>");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = null;
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            options.ListenLocalhost(port);
        });

        var app = builder.Build();

        app.Run(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = body.Length;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.Body.WriteAsync(body);
        });

        Console.WriteLine($"origin listening on 127.0.0.1:{port} bytes={body.Length}");
        Console.Out.Flush();

        app.Run();
        return 0;
    }
}
