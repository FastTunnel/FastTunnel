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
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Models;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.FileSystemGlobbing;

namespace FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

public class FastTunelProtocol
{
    public FastTunelProtocol(ConnectionContext context, FastTunnelServer fastTunnelServer)
    {
        this.context = context;
        this.fastTunnelServer = fastTunnelServer;
    }

    bool IsFastTunnel => Method == ProtocolConst.HTTP_METHOD_SWAP || MatchWeb != null;

    public WebInfo MatchWeb { get; private set; }

    ConnectionContext context { get; }

    IDuplexPipe Transport => context.Transport;

    internal async Task TryAnalysisPipeAsync()
    {
        var _input = Transport.Input;

        ReadResult result;
        ReadOnlySequence<byte> readableBuffer;
        while (true)
        {
            result = await _input.ReadAsync(context.ConnectionClosed);
            var tempBuffer = readableBuffer = result.Buffer;

            SequencePosition? position = null;

            do
            {
                position = tempBuffer.PositionOf((byte)'\n');

                if (position != null)
                {
                    var readedPosition = readableBuffer.GetPosition(1, position.Value);
                    if (ProcessLine(tempBuffer.Slice(0, position.Value), out string line))
                    {
                        if (Method == ProtocolConst.HTTP_METHOD_SWAP)
                        {
                            _input.AdvanceTo(readedPosition, readedPosition);
                        }
                        else
                        {
                            _input.AdvanceTo(readableBuffer.Start, readableBuffer.Start);
                        }

                        if (IsFastTunnel)
                        {
                            context.Features.Set<IFastTunnelFeature>(new FastTunnelFeature()
                            {
                                MatchWeb = MatchWeb,
                                HasReadLInes = HasReadLInes,
                                Method = Method,
                                Host = Host,
                                MessageId = MessageId,
                            });
                        }
                        return;
                    }

                    tempBuffer = tempBuffer.Slice(readedPosition);
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
    public string MessageId;
    private bool isFirstLine = true;

    public IList<string> HasReadLInes { get; private set; } = new List<string>();
    public FastTunnelServer fastTunnelServer { get; }

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
    /// <param name="readOnlySequence"></param>
    /// <returns>Header读取完毕？</returns>
    private bool ProcessLine(ReadOnlySequence<byte> readOnlySequence, out string lineStr)
    {
        lineStr = Encoding.UTF8.GetString(readOnlySequence);
        HasReadLInes.Add(lineStr);

        if (isFirstLine)
        {
            Method = lineStr.Substring(0, lineStr.IndexOf(" ")).ToUpper();

            switch (Method)
            {
                case ProtocolConst.HTTP_METHOD_SWAP:
                    // 客户端发起消息互转
                    var endIndex = lineStr.IndexOf(" ", 7);
                    MessageId = lineStr.Substring(7, endIndex - 7);
                    break;
                default:
                    // 常规Http请求，需要检查Host决定是否进行代理
                    break;
            }

            isFirstLine = false;
        }
        else
        {
            if (lineStr.Equals("\r"))
            {
                if (Method == ProtocolConst.HTTP_METHOD_SWAP)
                {
                    // do nothing
                }
                else
                {
                    // 匹配Host，
                    if (fastTunnelServer.TryGetWebProxyByHost(Host, out var web))
                    {
                        MatchWeb = web;
                    }
                }

                return true;
            }

            switch (Method)
            {
                case ProtocolConst.HTTP_METHOD_SWAP:
                    // do nothing
                    break;
                default:
                    // 检查Host决定是否进行代理
                    // Host: test.test.cc:1270
                    var lower = lineStr.Trim('\r').ToLower();
                    if (lower.StartsWith("host:"))
                    {
                        Host = lower.Split(" ")[1];
                        if (Host.Contains(":"))
                        {
                            Host = Host.Split(":")[0];
                        }
                    }
                    break;
            }
        }

        return false;
    }
}
