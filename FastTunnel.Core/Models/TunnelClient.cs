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
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Models;

public class TunnelClient
{
    public WebSocket webSocket { get; private set; }

    /// <summary>
    /// 服务端端口号
    /// </summary>
    public int ConnectionPort { get; set; }

    private readonly FastTunnelServer fastTunnelServer;
    private readonly ILoginHandler loginHandler;
    private readonly ILogger<TunnelClient> logger;

    public IPAddress RemoteIpAddress { get; private set; }

    private readonly IList<WebInfo> webInfos = new List<WebInfo>();
    private readonly IList<ForwardInfo<ForwardHandlerArg>> forwardInfos = new List<ForwardInfo<ForwardHandlerArg>>();

    public TunnelClient(
        WebSocket webSocket, FastTunnelServer fastTunnelServer,
        ILoginHandler loginHandler, IPAddress remoteIpAddress, ILogger<TunnelClient> logger)
    {
        this.logger = logger;
        this.webSocket = webSocket;
        this.fastTunnelServer = fastTunnelServer;
        this.loginHandler = loginHandler;
        this.RemoteIpAddress = remoteIpAddress;
    }

    internal void AddWeb(WebInfo info)
    {
        webInfos.Add(info);
    }

    internal void AddForward(ForwardInfo<ForwardHandlerArg> forwardInfo)
    {
        forwardInfos.Add(forwardInfo);
    }

    /// <summary>
    /// 接收客户端的消息
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task ReviceAsync(CancellationToken cancellationToken)
    {
        var utility = new WebSocketUtility(webSocket, ProcessLine);
        await utility.ProcessLinesAsync(cancellationToken);
    }


    private async void ProcessLine(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        var cmd = Encoding.UTF8.GetString(line);
        await HandleCmdAsync(this, cmd, cancellationToken);
    }

    private async Task<bool> HandleCmdAsync(TunnelClient tunnelClient, string lineCmd, CancellationToken cancellationToken)
    {
        try
        {
            return await loginHandler.HandlerMsg(fastTunnelServer, tunnelClient, lineCmd.Substring(1), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理客户端消息失败：cmd={lineCmd} {ex}");
            return false;
        }
    }

    internal void Logout()
    {
        // forward监听终止
        if (forwardInfos != null)
        {
            foreach (var item in forwardInfos)
            {
                try
                {
                    item.Listener.Stop();
                }
                catch { }
            }
        }

        webSocket.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
    }
}
