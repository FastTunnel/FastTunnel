// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Forwarder.Kestrel.MiddleWare;
using FastTunnel.Core.Server;
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
        var loggerFactory = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<SwapConnectionMiddleware>();
        var loggerHttp = loggerFactory.CreateLogger<FastTunnelConnectionMiddleware>();

        listenOptions.Use(next => new FastTunnelConnectionMiddleware(next, loggerHttp, fastTunnelServer).OnConnectionAsync);
        listenOptions.Use(next => new SwapConnectionMiddleware(next, logger, fastTunnelServer).OnConnectionAsync);

        // 登录频率低，放在后面
        return listenOptions;
    }
}
