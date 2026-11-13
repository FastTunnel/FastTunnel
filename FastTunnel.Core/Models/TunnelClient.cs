// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Server;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Models;

public class TunnelClient
{
    /// <summary>链路空闲超过该时长判定为假死，主动断开让客户端重连。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(15);

    private long _lastActiveTicks;

    public WebSocket webSocket { get; private set; }

    /// <summary>
    /// 服务端端口号
    /// </summary>
    public int ConnectionPort { get; set; }

    /// <summary>
    /// 客户端声明的结构化协议版本；低于 <see cref="ControlProtocol.Version"/> 时按旧版文本下发。
    /// </summary>
    public int ProtocolVersion { get; set; } = ControlProtocol.LegacyVersion;

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
        this._lastActiveTicks = Environment.TickCount64;
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
    public async Task ReviceAsync(CancellationToken cancellationToken)
    {
        var utility = new WebSocketUtility(webSocket, ProcessLine);
        await utility.ProcessLinesAsync(cancellationToken);
    }

    /// <summary>
    /// 心跳看门狗。NAT 静默回收连接时 TCP 可能长时间不报错，
    /// 靠应用层活跃时间兜底，避免控制通道假死导致隧道一直不可用。
    /// </summary>
    public async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastActiveTicks);
                if (idleMs <= IdleTimeout.TotalMilliseconds)
                {
                    continue;
                }

                logger.LogWarning("客户端 {RemoteIp} 心跳超时 {Idle}s，断开连接", RemoteIpAddress, idleMs / 1000);
                webSocket.Abort();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private async void ProcessLine(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        if (line.Length == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastActiveTicks, Environment.TickCount64);

        var cmd = Encoding.UTF8.GetString(line);
        await HandleCmdAsync(this, cmd, cancellationToken);
    }

    private async Task<bool> HandleCmdAsync(TunnelClient tunnelClient, string lineCmd, CancellationToken cancellationToken)
    {
        try
        {
            if (lineCmd.Length == 0)
            {
                return false;
            }

            var type = (MessageType)lineCmd[0];
            var payload = lineCmd.Substring(1);

            switch (type)
            {
                case MessageType.LogIn:
                    return await loginHandler.HandlerMsg(fastTunnelServer, tunnelClient, payload, cancellationToken);

                case MessageType.Ping:
                    await webSocket.SendMessageAsync(
                        MessageType.Pong, new HeartbeatMessage { SentAt = Environment.TickCount64 }, cancellationToken);
                    return true;

                case MessageType.Pong:
                    return true;

                default:
                    logger.LogDebug("忽略未处理的控制消息 {Type}", type);
                    return true;
            }
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

        try
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                _ = webSocket.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // 已被 Abort 或对端先关闭，忽略
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
