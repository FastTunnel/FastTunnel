// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.Forwarder.Kestrel.Features;
using FastTunnel.Core.Models;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using FastTunnel.Core.Sockets;
using Microsoft.AspNetCore.Connections;

namespace FastTunnel.Core.Forwarder;

public class FastTunelProtocol
{
    public FastTunelProtocol(ConnectionContext context, FastTunnelServer fastTunnelServer)
    {
        this.context = context;
        this.fastTunnelServer = fastTunnelServer;
    }

    bool IsFastTunnel => Method == ProtocolConst.HTTP_METHOD_SWAP
        || Method == ProtocolConst.HTTP_METHOD_MUX
        || MatchWeb != null
        || hostBanned;

    /// <summary>
    /// Host 命中封禁黑名单。熔断已摘掉 WebList 映射，但请求仍必须生成 Feature，
    /// 交给 GuardMiddleware 回 451；否则会落到 Kestrel 变成 404，等于「停止传输」没有可观测的响应。
    /// </summary>
    private bool hostBanned;

    public WebInfo MatchWeb { get; private set; }

    ConnectionContext context { get; }

    IDuplexPipe Transport => context.Transport;

    const char CharR = '\r';

    private const byte ByteLF = (byte)'\n';

    /// <summary>
    /// 只保留审计 / 真实 IP 还原需要的请求头。Cookie、Authorization、Proxy-Authorization
    /// 等凭据头一律不入库，避免用户凭据被带进审计日志与后续的旁路送审。
    /// </summary>
    private static readonly HashSet<string> AuditedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "x-forwarded-for", "x-real-ip", "forwarded",
        "user-agent", "content-type", "content-length", "referer",
    };

    private const int MaxHeaders = 16;
    private const int MaxHeaderValueLength = 8192;

    private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

    internal async Task TryAnalysisPipeAsync()
    {
        var _input = Transport.Input;

        ReadResult result;
        ReadOnlySequence<byte> readableBuffer;

        while (true)
        {
            result = await _input.ReadAsync(context.ConnectionClosed);
            readableBuffer = result.Buffer;
            SequencePosition start = readableBuffer.Start;

            SequencePosition? position;
            do
            {
                position = readableBuffer.PositionOf(ByteLF);

                if (position != null)
                {
                    var readedPosition = readableBuffer.GetPosition(1, position.Value);
                    if (ProcessHeaderLine(readableBuffer.Slice(0, position.Value), out _))
                    {
                        // PROXY / MUX 的请求头之后紧跟的就是隧道字节流（MUX 甚至直接是二进制帧），
                        // 必须一起消费掉，否则残留的 CRLF 会污染帧解析。
                        if (Method == ProtocolConst.HTTP_METHOD_SWAP || Method == ProtocolConst.HTTP_METHOD_MUX)
                        {
                            _input.AdvanceTo(readedPosition, readedPosition);
                        }
                        else
                        {
                            _input.AdvanceTo(start, start);
                        }

                        // 真实访客 IP：只有来自可信代理时才采信转发类请求头
                        ClientIp = ClientIpResolver.Resolve(
                            (context.RemoteEndPoint as IPEndPoint)?.Address,
                            headers,
                            fastTunnelServer.ServerOption.CurrentValue.TrustedProxyIps);

                        if (IsFastTunnel)
                        {
                            context.Features.Set<IFastTunnelFeature>(new FastTunnelFeature()
                            {
                                MatchWeb = MatchWeb,
                                Method = Method,
                                Host = Host,
                                Path = Path,
                                Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                                ClientIp = ClientIp,
                                MessageId = MessageId,
                            });
                        }
                        return;
                    }

                    readableBuffer = readableBuffer.Slice(readedPosition);
                }
            }
            while (position != null && !readableBuffer.IsEmpty);

            if (result.IsCompleted)
            {
                _input.AdvanceTo(readableBuffer.End, readableBuffer.End);
                return;
            }
        }
    }

    public string Method;
    public string Host = null;

    /// <summary>请求行的路径部分（按原样保留，可能含 query）。</summary>
    public string? Path { get; private set; }

    /// <summary>保留下来的审计请求头，键统一小写，不含任何凭据头。</summary>
    public IReadOnlyDictionary<string, string> Headers => headers;

    /// <summary>还原后的真实访客 IP；不可信来源下即 TCP 对端 IP。</summary>
    public IPAddress? ClientIp { get; private set; }

    public Guid MessageId;
    private bool isFirstLine = true;

    /// <summary>
    /// 请求是否落在隧道自身的 <c>/mux</c> 端点上。
    /// 这类请求属于隧道控制/数据通道，不能按 Host 匹配内网站点，否则会被当成普通 HTTP 转走。
    /// </summary>
    private bool isMuxEndpoint;

    private FastTunnelServer fastTunnelServer { get; }

    /// <summary>
    ///
    /// GET / HTTP/1.1
    /// Host: test.test.cc:1270
    /// Connection: keep-alive
    /// Upgrade-Insecure-Requests: 1
    /// User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.4896.127 Safari/537.36
    /// Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9
    /// Accept-Encoding: gzip, deflate
    /// Accept-Language: zh-CN,zh;q=0.9,en;q=0.8
    /// 
    /// </summary>
    /// <param name="headerLine"></param>
    /// <returns>Header读取完毕？</returns>
    private bool ProcessHeaderLine(ReadOnlySequence<byte> headerLine, out string headerLineStr)
    {
        headerLineStr = Encoding.UTF8.GetString(headerLine);

        if (isFirstLine)
        {
            var firstSpace = headerLineStr.IndexOf(" ");
            Method = firstSpace > 0
                ? headerLineStr.Substring(0, firstSpace).ToUpper()
                : headerLineStr.ToUpper();

            // 请求行：METHOD {path} HTTP/1.1
            var secondSpace = firstSpace > 0 ? headerLineStr.IndexOf(' ', firstSpace + 1) : -1;
            Path = secondSpace > firstSpace
                ? headerLineStr.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
                : null;

            isMuxEndpoint = IsMuxEndpoint(Path);

            switch (Method)
            {
                case ProtocolConst.HTTP_METHOD_SWAP:
                case ProtocolConst.HTTP_METHOD_MUX:
                    // PROXY /{msgId} 或 MUX /{sessionId}
                    MessageId = ParseRequestId(headerLineStr, firstSpace);
                    break;
                default:
                    // 常规Http请求，需要检查Host决定是否进行代理
                    break;
            }

            isFirstLine = false;
        }
        else
        {
            // TrailerHeader
            if (headerLineStr.Equals("\r"))
            {
                return true;
            }

            switch (Method)
            {
                case ProtocolConst.HTTP_METHOD_SWAP:
                case ProtocolConst.HTTP_METHOD_MUX:
                    // do nothing
                    break;
                default:
                    // 常规 Http 请求，检查 Host 决定是否进行代理。
                    // 写法可能是 "Host: test.test.cc:1270"、"Host:test.cc" 或 "Host: [::1]:1270"，
                    // 冒号后不一定有空格，所以按第一个冒号切，不能用 Split(" ")[1]（会越界）。
                    var line = headerLineStr.TrimEnd(CharR);
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        break;
                    }

                    var name = line.Substring(0, colon).Trim();
                    var value = line.Substring(colon + 1).Trim();

                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    {
                        // 不能简单按 ':' 切：IPv6 字面量是 [::1]:1270，切出来会变成 "["
                        Host = NetUtil.HostOnly(value.ToLowerInvariant());

                        // 匹配 Host
                        if (!isMuxEndpoint && fastTunnelServer.TryGetWebProxyByHost(Host, out var web))
                        {
                            MatchWeb = web;
                        }
                        else if (!isMuxEndpoint && fastTunnelServer.IsHostBanned(Host))
                        {
                            hostBanned = true;
                        }
                    }

                    // 只留审计白名单内的头，超限或超长直接丢弃，避免畸形头把内存撑爆
                    if (AuditedHeaders.Contains(name)
                        && headers.Count < MaxHeaders
                        && value.Length <= MaxHeaderValueLength)
                    {
                        headers[name.ToLowerInvariant()] = value;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否命中隧道自身的多路复用端点。<c>/mux</c> 前缀之后必须是 '/' 或结束，
    /// 避免 <c>/muxxxx</c> 被误判。
    /// </summary>
    private static bool IsMuxEndpoint(string? path)
    {
        if (string.IsNullOrEmpty(path)
            || !path.StartsWith(ProtocolConst.MUX_PATH, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == ProtocolConst.MUX_PATH.Length || path[ProtocolConst.MUX_PATH.Length] == '/';
    }

    /// <summary>从 <c>METHOD /{guid} HTTP/1.1</c> 中取出 guid。</summary>
    private static Guid ParseRequestId(string requestLine, int firstSpace)
    {
        if (firstSpace <= 0)
        {
            throw new FormatException($"非法的请求行: {requestLine}");
        }

        var secondSpace = requestLine.IndexOf(' ', firstSpace + 1);
        var path = secondSpace > firstSpace
            ? requestLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
            : requestLine.Substring(firstSpace + 1);

        return Guid.Parse(path.TrimStart('/'));
    }
}
