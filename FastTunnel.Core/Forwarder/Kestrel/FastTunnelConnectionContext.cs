// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel;
internal class FastTunnelConnectionContext : ConnectionContext
{
    private readonly ConnectionContext _inner;
    private readonly FastTunnelServer fastTunnelServer;
    private readonly ILogger _logger;

    public FastTunnelConnectionContext(ConnectionContext context, FastTunnelServer fastTunnelServer, ILogger logger)
    {
        this.fastTunnelServer = fastTunnelServer;
        this._inner = context;
        this._logger = logger;
    }

    public override IDuplexPipe Transport { get => _inner.Transport; set => _inner.Transport = value; }

    public override string ConnectionId { get => _inner.ConnectionId; set => _inner.ConnectionId = value; }

    public override IFeatureCollection Features => _inner.Features;

    public override IDictionary<object, object> Items { get => _inner.Items; set => _inner.Items = value; }

    public bool IsFastTunnel => Method == "PROXY" || MatchWeb != null;

    public WebInfo MatchWeb { get; private set; }

    public override ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    private readonly ReadOnlySequence<byte> readableBuffer;

    /// <summary>
    /// 解析FastTunnel协议
    /// </summary>
    /// <returns>is for FastTunnel</returns>
    internal async Task<bool> TryAnalysisPipeAsync()
    {
        var reader = Transport.Input;

        ReadResult result;
        ReadOnlySequence<byte> readableBuffer;

        while (true)
        {
            result = await reader.ReadAsync();
            var tempBuffer = readableBuffer = result.Buffer;

            SequencePosition? position = null;

            do
            {
                position = tempBuffer.PositionOf((byte)'\n');

                if (position != null)
                {
                    if (ProcessLine(tempBuffer.Slice(0, position.Value)))
                    {
                        if (Method == "PROXY")
                        {
                            reader.AdvanceTo(position.Value, position.Value);
                        }
                        else
                        {
                            reader.AdvanceTo(readableBuffer.Start, readableBuffer.Start);
                        }

                        return false;
                    }

                    tempBuffer = tempBuffer.Slice(readableBuffer.GetPosition(1, position.Value));
                }
            }
            while (position != null && !readableBuffer.IsEmpty);

            if (result.IsCompleted)
            {
                reader.AdvanceTo(readableBuffer.End, readableBuffer.End);
                break;
            }
        }

        return false;
    }

    public string Method;
    public string Host = null;
    public string MessageId;
    private bool complete = false;
    private bool isFirstLine = true;

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
    private bool ProcessLine(ReadOnlySequence<byte> readOnlySequence)
    {
        var lineStr = Encoding.UTF8.GetString(readOnlySequence);
        Console.WriteLine($"[HandleLien] {lineStr}");
        if (isFirstLine)
        {
            Method = lineStr.Substring(0, lineStr.IndexOf(" ")).ToUpper();

            switch (Method)
            {
                case "PROXY":
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
                complete = true;

                if (Method != "PROXY")
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
                case "PROXY":
                    // 找msgid

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
