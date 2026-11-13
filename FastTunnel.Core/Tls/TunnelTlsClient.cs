// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Tls;

/// <summary>
/// 客户端侧 TLS 握手。控制通道走 ClientWebSocket（wss），
/// 回连通道与多路复用通道走这里，三者的证书校验策略完全一致。
/// </summary>
public static class TunnelTlsClient
{
    public static async Task<SslStream> AuthenticateAsync(
        Stream inner,
        SuiDaoServer server,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, TunnelCertificate.CreateCallback(server, logger));

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = server.ServerAddr,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, cancellationToken);

            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }
}
