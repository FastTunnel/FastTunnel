// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Threading.Tasks;
using FastTunnel.Core.Client;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel;

internal class HandleHttpConnectionMiddleware
{
    private readonly ConnectionDelegate next;
    private readonly ILogger<HandleHttpConnectionMiddleware> logger;
    private readonly FastTunnelServer fastTunnelServer;

    public HandleHttpConnectionMiddleware(ConnectionDelegate next, ILogger<HandleHttpConnectionMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        logger.LogInformation("=========OnConnectionAsync===========");
        var ftContext = new FastTunnelConnectionContext(context, fastTunnelServer, logger);
        await ftContext.TryAnalysisPipeAsync();

        logger.LogInformation("=========TryAnalysisPipeAsync END===========");
        await next(ftContext.IsFastTunnel ? ftContext : context);
    }
}
