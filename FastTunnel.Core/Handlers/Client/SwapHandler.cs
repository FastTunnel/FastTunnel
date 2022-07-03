// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Client;

public class SwapHandler : IClientHandler
{
    private readonly ILogger<SwapHandler> _logger;

    public SwapHandler(ILogger<SwapHandler> logger)
    {
        _logger = logger;
    }

    public int SwapCount = 0;

    public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken)
    {
        string requestId = null;
        try
        {
            Interlocked.Increment(ref SwapCount);
            var msgs = msg.Split('|');
            requestId = msgs[0];
            var address = msgs[1];
            _logger.LogDebug($"========Swap Start:{requestId}==========");

            using var serverStream = await createRemote(requestId, cleint, cancellationToken);
            using var localStream = await createLocal(requestId, address, cancellationToken);

            var taskX = serverStream.CopyToAsync(localStream, cancellationToken);
            var taskY = localStream.CopyToAsync(serverStream, cancellationToken);

            await Task.WhenAny(taskX, taskY).WaitAsync(cancellationToken);

            _logger.LogDebug($"[HandlerMsgAsync] success {requestId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Swap error {requestId}");
        }
        finally
        {
            Interlocked.Decrement(ref SwapCount);
            _logger.LogDebug($"========Swap End:{requestId}==========");
        }
    }

    private async Task<Stream> createLocal(string requestId, string localhost, CancellationToken cancellationToken)
    {
        var socket = await DnsSocketFactory.ConnectAsync(localhost.Split(":")[0], int.Parse(localhost.Split(":")[1]));
        return new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };
    }

    private async Task<Stream> createRemote(string requestId, FastTunnelClient cleint, CancellationToken cancellationToken)
    {
        var socket = await DnsSocketFactory.ConnectAsync(cleint.Server.ServerAddr, cleint.Server.ServerPort);
        Stream serverStream = new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };

        if (cleint.Server.Protocol == "wss")
        {
            var sslStream = new SslStream(serverStream, false, delegate { return true; });
            await sslStream.AuthenticateAsClientAsync(cleint.Server.ServerAddr);
            serverStream = sslStream;
        }

        var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";

        var requestMsg = Encoding.UTF8.GetBytes(reverse);
        await serverStream.WriteAsync(requestMsg, cancellationToken);
        return serverStream;
    }
}
