// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Sockets;
using FastTunnel.Core.Tls;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Client;

/// <summary>
/// 客户端侧的多路复用连接：登录成功后额外建立一条长连接，
/// 之后服务端每来一条公网连接，只需要在这条连接上开一条流。
///
/// 承载方式由 <c>EnableMux/MuxTransport</c> 决定：
/// - tcp：裸 TCP（可套 TLS），默认；
/// - websocket：走 <c>/mux</c> 的 HTTP Upgrade，用于只能过 HTTP 的网络。
///
/// 若服务端不支持（版本较旧、或配置里关掉了 EnableMux），连接会被立刻关闭，
/// 这里判定为不支持并放弃重试，业务自动回退到旧的回连模式。
/// </summary>
public sealed class MuxClient
{
    /// <summary>建立后存活不足该时长就断开，视为「服务端不支持」。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>空闲保活间隔，避免 NAT 静默回收这条长连接。</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly FastTunnelClient _client;
    private readonly SwapHandler _swapHandler;
    private readonly ILogger<MuxClient> _logger;

    public MuxClient(FastTunnelClient client, SwapHandler swapHandler, ILogger<MuxClient> logger)
    {
        _client = client;
        _swapHandler = swapHandler;
        _logger = logger;
    }

    /// <summary>本轮控制连接生命周期内运行，控制面断开即停止。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = Environment.TickCount64;
            MuxConnectResult result;

            try
            {
                result = await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result == MuxConnectResult.NotSupported)
            {
                // 服务端压根没有这个端点/功能，再试也是白试（具体原因上面已经打过日志）
                _logger.LogInformation("[Mux]已停止尝试多路复用，回退到回连模式");
                return;
            }

            if (result == MuxConnectResult.Established
                && Environment.TickCount64 - startedAt < ProbeWindow.TotalMilliseconds)
            {
                // 握手成功却几乎立刻被断开：服务端不认识 MUX，别再徒劳重试
                _logger.LogInformation("[Mux]服务端未启用多路复用，回退到回连模式");
                return;
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 建立一次多路复用连接并跑完它的生命周期。
    /// </summary>
    private async Task<MuxConnectResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        MuxConnection connection;

        try
        {
            connection = await OpenSessionAsync(cancellationToken);
        }
        catch (MuxNotSupportedException ex)
        {
            _logger.LogWarning("[Mux]{Reason}，请改用 MuxTransport=tcp 或升级服务端", ex.Message);
            return MuxConnectResult.NotSupported;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 连不上/写不出去属于网络问题，与服务端是否支持多路复用无关
            _logger.LogDebug(ex, "[Mux]建立连接失败");
            return MuxConnectResult.Failed;
        }

        try
        {
            var session = new MuxSession(connection.Pipe, isServer: false, _logger,
                (stream, request) => OnAcceptAsync(stream, request, cancellationToken));

            // 保活任务随会话一起结束，不能 await——它是无限循环，会拖住探测判定
            _ = KeepAliveLoopAsync(session, cancellationToken);
            await session.RunAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }

        return MuxConnectResult.Established;
    }

    private bool UseWebSocket
        => string.Equals(_client.MuxTransport, "websocket", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_client.MuxTransport, "ws", StringComparison.OrdinalIgnoreCase);

    private Task<MuxConnection> OpenSessionAsync(CancellationToken cancellationToken)
        => UseWebSocket
            ? OpenWebSocketSessionAsync(cancellationToken)
            : OpenTcpSessionAsync(cancellationToken);

    private async Task<MuxConnection> OpenTcpSessionAsync(CancellationToken cancellationToken)
    {
        var server = _client.Server;

        // 双栈解析交给 NetUtil：域名 / IPv4 / IPv6 字面量都能连，且带 NoDelay
        var socket = await NetUtil.ConnectTcpAsync(server.ServerAddr, server.ServerPort, cancellationToken);

        try
        {
            _logger.LogInformation("建立多路复用连接 {Addr}:{Port}", server.ServerAddr, server.ServerPort);

            Stream stream = new NetworkStream(socket, ownsSocket: true);

            // 多路复用承载全部转发流量，必须与控制通道同等级加密
            if (server.UseTls)
            {
                stream = await TunnelTlsClient.AuthenticateAsync(stream, server, _logger, cancellationToken);
            }

            var duplex = new SocketDuplexPipe(stream);

            // 握手沿用与 PROXY 一致的形式，服务端在连接层按首行方法名分派
            var host = NetUtil.FormatHost(server.ServerAddr);
            var handshake = $"MUX /{_client.MuxId} HTTP/1.1\r\nHost: {host}:{server.ServerPort}\r\n\r\n";
            await duplex.Output.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);
            await duplex.Output.FlushAsync(cancellationToken);

            return new MuxConnection(duplex, socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// WebSocket 承载：一次标准的 HTTP Upgrade，握手完成后连接上跑的就是 MUX 帧。
    /// 与 TCP 版相比没有 <c>MUX /id</c> 首行——会话标识放在 URL 里，交给 HTTP 路由。
    /// </summary>
    private async Task<MuxConnection> OpenWebSocketSessionAsync(CancellationToken cancellationToken)
    {
        var server = _client.Server;
        var scheme = server.UseTls ? "wss" : "ws";

        // IPv6 字面量必须写成 [::1]，否则 Uri 解析失败
        var host = NetUtil.FormatHost(server.ServerAddr);
        var uri = new Uri($"{scheme}://{host}:{server.ServerPort}{ProtocolConst.MUX_PATH}/{_client.MuxId:N}");

        var socket = new ClientWebSocket();

        // 与控制通道同一套证书策略：指纹固定 > 系统 CA > 显式允许不受信（仅开发）
        socket.Options.RemoteCertificateValidationCallback = TunnelCertificate.CreateCallback(server, _logger);
        socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_VERSION, AssemblyUtility.GetVersion().ToString());

        if (!string.IsNullOrEmpty(_client.Token))
        {
            socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_TOKEN, _client.Token);
        }

        _logger.LogInformation("建立多路复用连接（WebSocket）{Uri}", uri);

        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
        }
        catch (Exception ex) when (IsHandshakeRejected(ex))
        {
            socket.Dispose();
            throw new MuxNotSupportedException($"服务端未接受 {ProtocolConst.MUX_PATH} 的 WebSocket 升级（{ex.Message}）");
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new MuxConnection(new WebSocketDuplexPipe(socket));
    }

    /// <summary>
    /// 只有「握手被拒」（非 101）才算服务端不支持；网络不通、超时都属于可重试的失败。
    /// </summary>
    private static bool IsHandshakeRejected(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is WebSocketException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task KeepAliveLoopAsync(MuxSession session, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await session.SendPingAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]保活发送失败");
        }
    }

    /// <summary>服务端开了一条新流：连内网目标，然后把两侧对接起来。</summary>
    private async Task OnAcceptAsync(MuxStream stream, SwapRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("[Mux]新流 {StreamId} => {Host}:{Port} ({Proto})",
            stream.Id, request.Host, request.Port, request.Proto);

        try
        {
            await _swapHandler.BridgeAsync(request, stream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mux]流 {StreamId} 转发失败", stream.Id);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private enum MuxConnectResult
    {
        /// <summary>会话正常跑完（或跑了一段时间后断开）。</summary>
        Established,

        /// <summary>服务端没有该能力，应停止重试。</summary>
        NotSupported,

        /// <summary>网络原因失败，可以重试。</summary>
        Failed,
    }

    /// <summary>
    /// 一条建好的多路复用连接。TCP 版要显式回收 socket，WebSocket 版由管道自身负责。
    /// </summary>
    private sealed class MuxConnection : IAsyncDisposable
    {
        private readonly Socket? socket;

        public MuxConnection(IDuplexPipe pipe, Socket? socket = null)
        {
            Pipe = pipe;
            this.socket = socket;
        }

        public IDuplexPipe Pipe { get; }

        public async ValueTask DisposeAsync()
        {
            if (Pipe is IAsyncDisposable disposable)
            {
                try
                {
                    await disposable.DisposeAsync();
                }
                catch
                {
                }
            }

            socket?.Dispose();
        }
    }

    /// <summary>服务端没有 <c>/mux</c> 这个能力，重试无意义。</summary>
    private sealed class MuxNotSupportedException : Exception
    {
        public MuxNotSupportedException(string message) : base(message)
        {
        }
    }
}
