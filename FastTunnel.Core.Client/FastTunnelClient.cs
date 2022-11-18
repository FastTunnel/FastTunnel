// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Client;

public class FastTunnelClient : IFastTunnelClient
{
    private ClientWebSocket socket;

    protected readonly ILogger<FastTunnelClient> _logger;
    protected DefaultClientConfig ClientConfig { get; private set; }

    private readonly SwapHandler swapHandler;
    private readonly LogHandler logHandler;

    private static ReadOnlySpan<byte> EndSpan => new ReadOnlySpan<byte>(new byte[] { (byte)'\n' });

    public SuiDaoServer Server { get; protected set; }

    public FastTunnelClient(
        ILogger<FastTunnelClient> logger,
        SwapHandler newCustomerHandler,
        LogHandler logHandler,
        IOptionsMonitor<DefaultClientConfig> configuration)
    {
        ReadOnlySpan<int> span = new ReadOnlySpan<int>();
        _logger = logger;
        swapHandler = newCustomerHandler;
        this.logHandler = logHandler;
        ClientConfig = configuration.CurrentValue;
        Server = ClientConfig.Server;
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
                await ReceiveServerAsync(cancellationToken);
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
        }.ToJson();
    }


    protected async Task ReceiveServerAsync(CancellationToken cancellationToken)
    {
        var utility = new WebSocketUtility(socket, ProcessLine);
        await utility.ProcessLinesAsync(cancellationToken);
    }

    private void ProcessLine(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        HandleServerRequestAsync(line, cancellationToken);
    }

    private void HandleServerRequestAsync(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        try
        {
            var row = line.ToArray();
            var cmd = row[0];
            IClientHandler handler;
            switch ((MessageType)cmd)
            {
                case MessageType.SwapMsg:
                case MessageType.Forward:
                    handler = swapHandler;
                    break;
                case MessageType.Log:
                    handler = logHandler;
                    break;
                default:
                    throw new Exception($"未处理的消息：cmd={cmd}");
            }

#if NETCOREAPP3_1
            var content = Encoding.UTF8.GetString(line.Slice(1).ToArray());
#endif

#if NET5_0_OR_GREATER
            var content = Encoding.UTF8.GetString(line.Slice(1));
#endif
            handler.HandlerMsgAsync(this, content, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("===== FastTunnel Client Stoping =====");
        if (socket != null)
        {
            socket.Abort();
        }
    }
}
