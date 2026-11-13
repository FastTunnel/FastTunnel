// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Client;

public class FastTunnelClient : IFastTunnelClient
{
    /// <summary>心跳间隔。服务端空闲判定为 90s，可容忍连续两个心跳丢失。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>连续这么久没收到服务端任何消息（含 Pong），判定下行假死并重连。</summary>
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(90);

    private ClientWebSocket socket;

    private long _lastReceiveTicks;

    protected readonly ILogger<FastTunnelClient> _logger;
    protected DefaultClientConfig ClientConfig { get; private set; }

    private readonly SwapHandler swapHandler;
    private readonly LogHandler logHandler;
    private readonly MuxClient muxClient;

    /// <summary>
    /// 本客户端的多路复用标识，登录时上报给服务端，随后用同一个 id 建立 MUX 连接。
    /// </summary>
    public Guid MuxId { get; } = Guid.NewGuid();

    public SuiDaoServer Server { get; protected set; }

    public FastTunnelClient(
        ILogger<FastTunnelClient> logger,
        SwapHandler newCustomerHandler,
        LogHandler logHandler,
        ILoggerFactory loggerFactory,
        IOptionsMonitor<DefaultClientConfig> configuration)
    {
        _logger = logger;
        swapHandler = newCustomerHandler;
        this.logHandler = logHandler;
        ClientConfig = configuration.CurrentValue;
        Server = ClientConfig.Server;
        muxClient = new MuxClient(this, newCustomerHandler, loggerFactory.CreateLogger<MuxClient>());
    }

    /// <summary>
    /// 启动客户端
    /// </summary>
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("===== FastTunnel Client Start =====");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await loginAsync(cancellationToken);

                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiving = ReceiveServerAsync(connectionCts.Token);
                var heartbeat = HeartbeatLoopAsync(connectionCts.Token);

                // 多路复用连接独立重连，失败不影响控制面；控制面断开时随 connectionCts 一起停止。
                if (ClientConfig.EnableMux)
                {
                    _ = muxClient.RunAsync(connectionCts.Token);
                }

                // 任一任务结束（连接断开、心跳失败、收到取消）都触发本连接收尾并重连。
                await Task.WhenAny(receiving, heartbeat);

                connectionCts.Cancel();
                await AwaitQuietlyAsync(receiving, heartbeat);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        _logger.LogInformation("===== FastTunnel Client End =====");
    }

    private async Task loginAsync(CancellationToken cancellationToken)
    {
        var logMsg = GetLoginMsg(cancellationToken);
        if (socket != null)
        {
            socket.Abort();
        }

        // 连接到的目标IP
        socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = delegate { return true; };
        socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_VERSION, AssemblyUtility.GetVersion().ToString());
        socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_TOKEN, ClientConfig.Token);
        Interlocked.Exchange(ref _lastReceiveTicks, Environment.TickCount64);

        _logger.LogInformation($"正在连接服务端 {Server.ServerAddr}:{Server.ServerPort}");
        await socket.ConnectAsync(
            new Uri($"{Server.Protocol}://{Server.ServerAddr}:{Server.ServerPort}"), cancellationToken);

        _logger.LogDebug("连接服务端成功");

        // 登录
        await socket.SendCmdAsync(MessageType.LogIn, logMsg, cancellationToken);
    }

    protected virtual string GetLoginMsg(CancellationToken cancellationToken)
    {
        Server = ClientConfig.Server;
        return new LogInMassage
        {
            Webs = ClientConfig.Webs,
            Forwards = ClientConfig.Forwards,
            ProtocolVersion = ControlProtocol.Version,
            MuxId = ClientConfig.EnableMux ? MuxId : Guid.Empty,
        }.ToJson();
    }

    /// <summary>
    /// 定时发送 Ping。发送失败或连接关闭即结束，交由 StartAsync 触发重连，
    /// 避免 NAT 静默回收连接后客户端长时间无感知。
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // 双向看门狗：能发出 Ping 不代表能收到 Pong，下行假死时靠这层发现。
                var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastReceiveTicks);
                if (idleMs > ReceiveTimeout.TotalMilliseconds)
                {
                    _logger.LogWarning("控制通道 {Idle}s 未收到服务端消息，断开重连", idleMs / 1000);
                    socket.Abort();
                    return;
                }

                await socket.SendMessageAsync(
                    MessageType.Ping,
                    new HeartbeatMessage { SentAt = Environment.TickCount64 },
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    protected async Task ReceiveServerAsync(CancellationToken cancellationToken)
    {
        var utility = new WebSocketUtility(socket, ProcessLine);
        await utility.ProcessLinesAsync(cancellationToken);
    }

    private void ProcessLine(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastReceiveTicks, Environment.TickCount64);
        _ = HandleServerRequestAsync(line, cancellationToken);
    }

    private async Task HandleServerRequestAsync(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        try
        {
            if (line.Length == 0)
            {
                return;
            }

            var cmd = line.FirstSpan[0];
            var content = Encoding.UTF8.GetString(line.Slice(1));

            switch ((MessageType)cmd)
            {
                case MessageType.SwapMsg:
                case MessageType.Forward:
                    await swapHandler.HandlerMsgAsync(this, content, cancellationToken);
                    break;

                case MessageType.Log:
                    await logHandler.HandlerMsgAsync(this, content, cancellationToken);
                    break;

                case MessageType.Ping:
                    await socket.SendMessageAsync(
                        MessageType.Pong,
                        new HeartbeatMessage { SentAt = Environment.TickCount64 },
                        cancellationToken);
                    break;

                case MessageType.Pong:
                    break;

                default:
                    throw new Exception($"未处理的消息：cmd={cmd}");
            }
        }
        catch (OperationCanceledException)
        {
            // 连接关闭时的正常路径
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HandleServerRequest Error]");
        }
    }

    private static async Task AwaitQuietlyAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // 断线、取消都是预期路径
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("===== FastTunnel Client Stoping =====");
        if (socket != null)
        {
            socket.Abort();
        }

        await Task.CompletedTask;
    }
}
