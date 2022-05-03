// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel;
internal class ClientConnectionMiddleware
{
    readonly ConnectionDelegate next;
    readonly ILogger<ClientConnectionMiddleware> logger;
    FastTunnelServer fastTunnelServer;

    public ClientConnectionMiddleware(ConnectionDelegate next, ILogger<ClientConnectionMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var oldTransport = context.Transport;

        try
        {
            if (!await ReadPipeAsync(context))
            {
                await next(context);
            }

            await next(context);
        }
        finally
        {
            context.Transport = oldTransport;
        }
    }

    async Task<bool> ReadPipeAsync(ConnectionContext context)
    {
        var reader = context.Transport.Input;

        var isProxy = false;
        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;
            SequencePosition? position = null;

            do
            {
                position = buffer.PositionOf((byte)'\n');

                if (position != null)
                {
                    isProxy = ProcessProxyLine(buffer.Slice(0, position.Value));
                    if (isProxy)
                    {
                        await Login(buffer, position.Value, context);
                        return true;
                    }
                    else
                    {
                        context.Transport.Input.AdvanceTo(buffer.Start, buffer.Start);
                        return false;
                    }
                }
            }
            while (position != null);

            if (result.IsCompleted)
            {
                break;
            }
        }

        return false;
    }

    private async Task Login(ReadOnlySequence<byte> buffer, SequencePosition position, ConnectionContext context)
    {


    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="readOnlySequence"></param>
    private bool ProcessProxyLine(ReadOnlySequence<byte> readOnlySequence)
    {
        var str = Encoding.UTF8.GetString(readOnlySequence);

        return str.StartsWith("LOGIN");
    }
}

