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
using FastTunnel.Core.Forwarder.MiddleWare;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel;

internal class SwapConnectionMiddleware
{
    readonly ConnectionDelegate next;
    readonly ILogger<SwapConnectionMiddleware> logger;
    FastTunnelServer fastTunnelServer;

    public SwapConnectionMiddleware(ConnectionDelegate next, ILogger<SwapConnectionMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var oldTransport = context.Transport;

        if (!await ReadPipeAsync(context))
        {
            await next(context);
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
                        await Swap(buffer, position.Value, context);
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

    private async Task Swap(ReadOnlySequence<byte> buffer, SequencePosition position, ConnectionContext context)
    {
        var firstLineBuffer = buffer.Slice(0, position);
        var firstLine = Encoding.UTF8.GetString(firstLineBuffer);

        // PROXY /c74eb488a0f54d888e63d85c67428b52 HTTP/1.1
        var endIndex = firstLine.IndexOf(" ", 7);
        var requestId = firstLine.Substring(7, endIndex - 7);
        Console.WriteLine($"[开始进行Swap操作] {requestId}");

        context.Transport.Input.AdvanceTo(buffer.GetPosition(1, position), buffer.GetPosition(1, position));

        if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseForYarp))
        {
            logger.LogError($"[PROXY]:RequestId不存在 {requestId}");
            return;
        };

        using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseForYarp.TrySetResult(reverseConnection);

        var lifetime = context.Features.Get<IConnectionLifetimeFeature>();

        var closedAwaiter = new TaskCompletionSource<object>();

        lifetime.ConnectionClosed.Register((task) =>
        {
            (task as TaskCompletionSource<object>).SetResult(null);
        }, closedAwaiter);

        try
        {
            await closedAwaiter.Task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "");
        }
        finally
        {
            context.Transport.Input.Complete();
            context.Transport.Output.Complete();
            logger.LogInformation($"=====================Swap End:{requestId}================== ");
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="readOnlySequence"></param>
    private bool ProcessProxyLine(ReadOnlySequence<byte> readOnlySequence)
    {
        var str = Encoding.UTF8.GetString(readOnlySequence);

        return str.StartsWith("PROXY");
    }
}
