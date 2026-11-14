// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Forwarder.Kestrel.MiddleWare;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Extensions;

public static class ListenOptionsSwapExtensions
{
    /// <summary>
    /// 使用FastTunnel中间件
    /// </summary>
    /// <param name="listenOptions"></param>
    /// <returns></returns>
    public static ListenOptions UseConnectionFastTunnel(this ListenOptions listenOptions)
    {
        var fastTunnelServer = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<FastTunnelServer>();
        var certificateProvider = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<ServerCertificateProvider>();
        var loggerFactory = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var forwardLogger = loggerFactory.CreateLogger<ForwarderMiddleware>();
        var initLogger = loggerFactory.CreateLogger<InitializerMiddleware>();
        var guardLogger = loggerFactory.CreateLogger<GuardMiddleware>();
        var tlsLogger = loggerFactory.CreateLogger<TlsMiddleware>();
        var bans = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<BanRegistry>();
        var audit = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<AuditLog>();
        var traffic = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<TrafficMeter>();

        // TLS 必须是最外层：先按首字节分流，命中 TLS 的连接原地解密，
        // 之后协议识别 / MUX / Kestrel HTTP 都工作在明文流上。
        listenOptions.Use(next => new TlsMiddleware(next, tlsLogger, certificateProvider).OnConnectionAsync);
        listenOptions.Use(next => new InitializerMiddleware(next, initLogger, fastTunnelServer).OnConnectionAsync);
        // Guard 必须在协议解析之后、转发分叉之前：既拿得到 Host / 真实 IP，又能一次拦住两条数据面
        listenOptions.Use(next => new GuardMiddleware(next, guardLogger, fastTunnelServer, bans, audit, traffic).OnConnectionAsync);
        listenOptions.Use(next => new ForwarderMiddleware(next, forwardLogger, fastTunnelServer, traffic).OnConnectionAsync);

        // 启动即准备证书：配置错误当场报错，指纹也能第一时间打进日志
        certificateProvider.EnsureReady();

        return listenOptions;
    }
}
