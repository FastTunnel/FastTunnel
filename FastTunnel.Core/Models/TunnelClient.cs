// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;

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

    public IPAddress RemoteIpAddress { get; private set; }

    private readonly IList<WebInfo> webInfos = new List<WebInfo>();
    private readonly IList<ForwardInfo<ForwardHandlerArg>> forwardInfos = new List<ForwardInfo<ForwardHandlerArg>>();

    public TunnelClient(WebSocket webSocket, FastTunnelServer fastTunnelServer, ILoginHandler loginHandler, IPAddress remoteIpAddress)
    {
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

    public async Task ReviceAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ProtocolConst.MAX_CMD_LENGTH];
        var tunnelProtocol = new TunnelProtocol();

        while (true)
        {
            var res = await webSocket.ReceiveAsync(buffer, cancellationToken);
            var cmds = tunnelProtocol.HandleBuffer(buffer, 0, res.Count);
            if (cmds == null) continue;

            foreach (var item in cmds)
            {
                if (!await HandleCmdAsync(this, item))
                {
                    return;
                };
            }
        }
    }

    private async Task<bool> HandleCmdAsync(TunnelClient tunnelClient, string lineCmd)
    {
        try
        {
            return await loginHandler.HandlerMsg(fastTunnelServer, tunnelClient, lineCmd.Substring(1));
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
