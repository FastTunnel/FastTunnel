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
        var forwardLogger = loggerFactory.CreateLogger<ForwarderMiddleware>();
        var initLogger = loggerFactory.CreateLogger<InitializerMiddleware>();

        listenOptions.Use(next => new InitializerMiddleware(next, initLogger, fastTunnelServer).OnConnectionAsync);
        listenOptions.Use(next => new ForwarderMiddleware(next, forwardLogger, fastTunnelServer).OnConnectionAsync);

        return listenOptions;
    }
}
