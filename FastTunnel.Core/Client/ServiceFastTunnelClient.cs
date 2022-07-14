// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Client;

public class ServiceFastTunnelClient : IHostedService
{
    private readonly ILogger<ServiceFastTunnelClient> _logger;
    private readonly IFastTunnelClient _fastTunnelClient;

    public ServiceFastTunnelClient(ILogger<ServiceFastTunnelClient> logger, IFastTunnelClient fastTunnelClient)
    {
        _logger = logger;
        _fastTunnelClient = fastTunnelClient;

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _fastTunnelClient.StartAsync(cancellationToken);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _fastTunnelClient.StopAsync(cancellationToken);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            _logger.LogError("【UnhandledException】" + e.ExceptionObject);
            var type = e.ExceptionObject.GetType();
            _logger.LogError("ExceptionObject GetType " + type);
        }
        catch
        {
        }
    }
}
