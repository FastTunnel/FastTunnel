// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Forwarder.MiddleWare;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Extensions;

public static class ListenOptionsSwapExtensions
{
    public static ListenOptions UseFastTunnelSwap(this ListenOptions listenOptions)
    {
        var loggerFactory = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<SwapConnectionMiddleware>();
        var fastTunnelServer = listenOptions.KestrelServerOptions.ApplicationServices.GetRequiredService<FastTunnelServer>();

        listenOptions.Use(next => new SwapConnectionMiddleware(next, logger, fastTunnelServer).OnConnectionAsync);
        return listenOptions;
    }
}