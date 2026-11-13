// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading.Tasks;
using FastTunnel.Core.Refs;
using FastTunnel.Core.Tls;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

/// <summary>
/// 隧道客户端的 TLS 终结层。
///
/// 服务端 1270 端口同时承载两类流量：
/// - 公网用户访问内网站点的明文 HTTP；
/// - 隧道客户端的控制 / 回连 / 多路复用连接。
///
/// 因此不能把整个端口配置成 HTTPS。这里在连接最前面按首字节分流：
/// TLS ClientHello 固定以 0x16 开头，其余（GET / POST / PROXY / MUX ...）原样交给后续中间件。
/// 命中 TLS 的连接就地完成握手，再把解密后的字节流重新挂回 <c>context.Transport</c>。
/// </summary>
internal class TlsMiddleware
{
    /// <summary>TLS 握手记录类型（ClientHello）。</summary>
    private const byte TlsHandshakeRecord = 0x16;

    private readonly ConnectionDelegate next;
    private readonly ILogger<TlsMiddleware> logger;
    private readonly ServerCertificateProvider certificateProvider;

    public TlsMiddleware(
        ConnectionDelegate next,
        ILogger<TlsMiddleware> logger,
        ServerCertificateProvider certificateProvider)
    {
        this.next = next;
        this.logger = logger;
        this.certificateProvider = certificateProvider;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var input = context.Transport.Input;

        ReadResult result;
        while (true)
        {
            result = await input.ReadAsync(context.ConnectionClosed);
            if (!result.Buffer.IsEmpty || result.IsCompleted)
            {
                break;
            }
        }

        var buffer = result.Buffer;
        if (buffer.IsEmpty)
        {
            // 未检查任何数据：下次 ReadAsync 会立刻把缓冲里的数据交出来
            input.AdvanceTo(buffer.Start, buffer.Start);
            await next(context);
            return;
        }

        if (buffer.FirstSpan[0] != TlsHandshakeRecord)
        {
            // 明文流量：一个字节都不消费、也不算已检查，交回原有链路。
            // 注意 examined 必须给 Start——给 End 等于告诉 Pipe「我看完了」，
            // 下一次 ReadAsync 就会一直等新数据，明文请求会直接卡死。
            input.AdvanceTo(buffer.Start, buffer.Start);
            await next(context);
            return;
        }

        // 命中 TLS
        if (!certificateProvider.Options.Enabled)
        {
            // 关闭 TLS 时若把 ClientHello 交给后面的 HTTP 解析器，连接会一直挂着，
            // 客户端只能等超时。这里直接断开，让客户端尽快报错。
            logger.LogWarning("收到 TLS 连接但服务端未启用 TLS（FastTunnel:Tls:Enabled=false），已断开。客户端请设置 Server:Protocol=ws");
            input.AdvanceTo(buffer.Start, buffer.Start);
            await CloseQuietlyAsync(context);
            return;
        }

        // TLS 分支同理：数据要留给 SslStream 去读，不能标记为已检查
        input.AdvanceTo(buffer.Start, buffer.Start);

        SslStream ssl;
        try
        {
            var rawStream = new DuplexPipeStream(context.Transport.Input, context.Transport.Output);
            ssl = new SslStream(rawStream, leaveInnerStreamOpen: false);

            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificateProvider.GetCertificate(),
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, context.ConnectionClosed);
        }
        catch (Exception ex) when (ex is AuthenticationException or OperationCanceledException or System.IO.IOException)
        {
            // 客户端握手失败（证书不认、中间有代理、扫描器等），属正常噪声
            logger.LogDebug(ex, "客户端 TLS 握手失败 {Remote}", context.RemoteEndPoint);
            await CloseQuietlyAsync(context);
            return;
        }

        logger.LogDebug("客户端 TLS 握手完成 {Remote}", context.RemoteEndPoint);

        await using var pipe = new TlsDuplexPipe(ssl);

        // 之后的中间件（协议识别、MUX、Kestrel HTTP）看到的是解密后的字节流
        var plaintext = context.Transport;
        context.Transport = pipe;

        try
        {
            await next(context);
        }
        finally
        {
            context.Transport = plaintext;
        }
    }

    private static async Task CloseQuietlyAsync(ConnectionContext context)
    {
        try
        {
            await context.Transport.Input.CompleteAsync();
            await context.Transport.Output.CompleteAsync();
        }
        catch
        {
            // 连接可能已被对端关闭
        }
    }
}
