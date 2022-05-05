// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel;

internal class HandleHttpConnectionMiddleware
{
    readonly ConnectionDelegate next;
    readonly ILogger<HandleHttpConnectionMiddleware> logger;
    FastTunnelServer fastTunnelServer;

    public HandleHttpConnectionMiddleware(ConnectionDelegate next, ILogger<HandleHttpConnectionMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var ftContext = new FastTunnelConnectionContext(context, logger);
        var fasttunnelHandle = await ftContext.TryAnalysisPipeAsync();

        await next(ftContext);
    }
}
