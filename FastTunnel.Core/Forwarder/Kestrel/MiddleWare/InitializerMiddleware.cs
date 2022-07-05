// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Threading.Tasks;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

/// <summary>
/// 预处理中间件
/// </summary>
internal class InitializerMiddleware
{
    private readonly ConnectionDelegate next;
    private readonly ILogger<InitializerMiddleware> logger;
    private readonly FastTunnelServer fastTunnelServer;

    public InitializerMiddleware(ConnectionDelegate next, ILogger<InitializerMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        logger.LogDebug("=========TryAnalysisPipeAsync SART===========");
        await new FastTunelProtocol(context, fastTunnelServer).TryAnalysisPipeAsync();
        logger.LogDebug("=========TryAnalysisPipeAsync END===========");

        await next(context);
    }
}
