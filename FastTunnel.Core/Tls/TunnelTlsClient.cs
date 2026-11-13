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
using FastTunnel.Core.Sockets;
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
                // 传 IP 字面量（含 IPv6）时 SslStream 不会发送 SNI，证书校验走 SAN 的 IP 项；
                // 这里统一去掉方括号，避免 "[::1]" 被当成主机名去匹配 DNS 名。
                TargetHost = NetUtil.StripBrackets(server.ServerAddr),
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
