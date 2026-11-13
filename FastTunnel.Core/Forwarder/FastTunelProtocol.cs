// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Kestrel.Features;
using FastTunnel.Core.Models;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
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
        || MatchWeb != null;

    public WebInfo MatchWeb { get; private set; }

    ConnectionContext context { get; }

    IDuplexPipe Transport => context.Transport;

    const char CharR = '\r';

    private const byte ByteCR = (byte)'\r';
    private const byte ByteLF = (byte)'\n';
    private const byte ByteColon = (byte)':';
    private const byte ByteSpace = (byte)' ';

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

                        if (IsFastTunnel)
                        {
                            context.Features.Set<IFastTunnelFeature>(new FastTunnelFeature()
                            {
                                MatchWeb = MatchWeb,
                                Method = Method,
                                Host = Host,
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

    /// <summary>请求行里的路径（不含 query）。</summary>
    public string? Path { get; private set; }

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
                    // 检查Host决定是否进行代理
                    // Host: test.test.cc:1270
                    var lower = headerLineStr.Trim(CharR).ToLower();
                    if (lower.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
                    {
                        Host = lower.Split(" ")[1];
                        if (Host.Contains(":"))
                        {
                            Host = Host.Split(":")[0];
                        }

                        // 匹配Host，
                        if (!isMuxEndpoint && fastTunnelServer.TryGetWebProxyByHost(Host, out var web))
                        {
                            MatchWeb = web;
                        }
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
