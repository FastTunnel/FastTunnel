// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Forwarder.Streams;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Listener;

/// <summary>
/// UDP 端口转发监听器。
/// 通过对每个公网 UDP 来源端点维护一条与 FastTunnel 客户端之间的 swap 流，
/// 在该流上以 [2 字节大端长度 + 数据] 的方式串行化 UDP 数据报。
/// </summary>
public class UdpProxyListener : IPortListener
{
    /// <summary>
    /// 会话空闲超时时间，超过此时间没有数据则关闭会话
    /// </summary>
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger;
    private readonly UdpClient _udpServer;
    private readonly SocketAddress _remoteAddress;
    private readonly EndPoint _remoteEndpointPrototype;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, UdpSession> _sessions = new();
    private ForwardDispatcher _dispatcher;
    private bool _shutdown;

    public string ListenIp { get; }

    public int ListenPort { get; }

    public UdpProxyListener(string ip, int port, ILogger logger, ForwardDispatcher dispatcher)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        ListenPort = port;

        // 与 TCP 端口转发一致：通配地址用双栈，一个 socket 同时收 v4/v6 数据报。
        // 双栈 socket 上 IPv4 来源会表现为 ::ffff:x.x.x.x，作为会话 key 回发同样有效，
        // 因此只在日志里还原成正常写法，不改变实际端点。
        _udpServer = NetUtil.BindUdp(NetUtil.ParseListenAddress(ip), port);

        // 复用同一个 SocketAddress 接收，避免 UdpClient.ReceiveAsync 每包 new byte[]。
        // 双栈 socket 上 IPv4 对端会是 ::ffff:x.x.x.x，原型必须跟随实际 family。
        var anyAddress = _udpServer.Client.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any;
        _remoteAddress = new IPEndPoint(anyAddress, 0).Serialize();
        _remoteEndpointPrototype = new IPEndPoint(anyAddress, 0);

        ListenIp = _udpServer.Client.AddressFamily == AddressFamily.InterNetworkV6
            ? (_udpServer.Client.DualMode ? "::" : "[::]")
            : "0.0.0.0";
    }

    public void Start()
    {
        _shutdown = false;
        _ = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        // 一次租 reusable buffer，收包不再每包分配 byte[]。
        var buffer = ArrayPool<byte>.Shared.Rent(UdpDatagramFramer.MaxDatagramSize);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = await _udpServer.Client.ReceiveFromAsync(
                        buffer, SocketFlags.None, _remoteAddress, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, $"[UdpForward]ReceiveFromAsync error on {ListenIp}:{ListenPort}");
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, $"[UdpForward]ReceiveFromAsync error on {ListenIp}:{ListenPort}");
                    continue;
                }

                // SocketAddress 会被下一次收包覆盖，会话 key 必须取一份快照。
                var remoteEp = (IPEndPoint)_remoteEndpointPrototype.Create(_remoteAddress);

                try
                {
                    var session = _sessions.GetOrAdd(remoteEp, ep => CreateSession(ep));
                    await session.SendToTunnelAsync(buffer.AsMemory(0, received), _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, $"[UdpForward]Dispatch error from {NetUtil.Display(remoteEp)}");
                    if (_sessions.TryRemove(remoteEp, out var dead))
                    {
                        dead.Dispose();
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private UdpSession CreateSession(IPEndPoint remoteEp)
    {
        var session = new UdpSession(remoteEp, _udpServer, _dispatcher, _logger, OnSessionClosed);
        _ = session.StartAsync(_cts.Token);
        return session;
    }

    private void OnSessionClosed(IPEndPoint ep)
    {
        if (_sessions.TryRemove(ep, out var s))
        {
            s.Dispose();
        }
    }

    public void Stop()
    {
        if (_shutdown) return;
        _shutdown = true;

        try { _cts.Cancel(); } catch { }

        foreach (var kv in _sessions)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _sessions.Clear();

        try { _udpServer.Close(); } catch { }
        try { _udpServer.Dispose(); } catch { }
    }

    /// <summary>
    /// 单个 UDP 来源端点对应一条 swap 隧道
    /// </summary>
    private sealed class UdpSession : IDisposable
    {
        private readonly IPEndPoint _remoteEp;
        private readonly UdpClient _udpServer;
        private readonly ForwardDispatcher _dispatcher;
        private readonly ILogger _logger;
        private readonly Action<IPEndPoint> _onClosed;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Stream _tunnelStream;
        private CancellationTokenSource _streamCts;
        private CancellationTokenSource _idleCts;
        private DateTime _lastActive = DateTime.UtcNow;
        private int _disposed;

        public UdpSession(IPEndPoint remoteEp, UdpClient udpServer, ForwardDispatcher dispatcher,
            ILogger logger, Action<IPEndPoint> onClosed)
        {
            _remoteEp = remoteEp;
            _udpServer = udpServer;
            _dispatcher = dispatcher;
            _logger = logger;
            _onClosed = onClosed;
        }

        public async Task StartAsync(CancellationToken parentToken)
        {
            try
            {
                var swap = await _dispatcher.RequestSwapAsync("udp", parentToken);
                _tunnelStream = swap.Stream;
                _streamCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken, swap.TokenSource.Token);
                _idleCts = CancellationTokenSource.CreateLinkedTokenSource(_streamCts.Token);

                _logger.LogDebug($"[UdpForward]Session opened for {NetUtil.Display(_remoteEp)}");
                _ready.TrySetResult(true);

                _ = Task.Run(() => ReadFromTunnelAsync(_idleCts.Token));
                _ = Task.Run(() => MonitorIdleAsync(_idleCts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]Session open failed for {NetUtil.Display(_remoteEp)}");
                _ready.TrySetException(ex);
                Dispose();
            }
        }

        public async Task SendToTunnelAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            if (payload.Length > UdpDatagramFramer.MaxDatagramSize)
            {
                _logger.LogDebug($"[UdpForward]Drop oversized datagram {payload.Length}");
                return;
            }

            await _ready.Task.WaitAsync(token).ConfigureAwait(false);
            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _lastActive = DateTime.UtcNow;
                await UdpDatagramFramer.WriteAsync(_tunnelStream, payload, token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadFromTunnelAsync(CancellationToken token)
        {
            // MuxStream / DuplexPipeStream 都暴露 IDuplexPipe，直接从管道切片发 UDP。
            if (_tunnelStream is IDuplexPipe pipe)
            {
                await ReadFromPipeAsync(pipe.Input, token).ConfigureAwait(false);
                return;
            }

            await ReadFromStreamAsync(token).ConfigureAwait(false);
        }

        private async Task ReadFromPipeAsync(PipeReader reader, CancellationToken token)
        {
            var scratch = ArrayPool<byte>.Shared.Rent(UdpDatagramFramer.MaxDatagramSize);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await reader.ReadAsync(token).ConfigureAwait(false);
                    var buffer = result.Buffer;
                    var consumed = buffer.Start;
                    try
                    {
                        while (UdpDatagramFramer.TryRead(buffer, scratch, out var payload, out var total))
                        {
                            await SendBackAsync(payload, token).ConfigureAwait(false);
                            buffer = buffer.Slice(total);
                            consumed = buffer.Start;
                        }
                    }
                    finally
                    {
                        reader.AdvanceTo(consumed, buffer.End);
                    }

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]ReadFromTunnel error for {NetUtil.Display(_remoteEp)}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
                _onClosed?.Invoke(_remoteEp);
            }
        }

        private async Task ReadFromStreamAsync(CancellationToken token)
        {
            var header = new byte[2];
            var buffer = ArrayPool<byte>.Shared.Rent(UdpDatagramFramer.MaxDatagramSize);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!await UdpDatagramFramer.ReadExactAsync(_tunnelStream, header, token).ConfigureAwait(false))
                        break;

                    var len = BinaryPrimitives.ReadUInt16BigEndian(header);
                    if (len > UdpDatagramFramer.MaxDatagramSize) break; // 协议异常，避免越界
                    if (len > 0 && !await UdpDatagramFramer.ReadExactAsync(
                        _tunnelStream, buffer.AsMemory(0, len), token).ConfigureAwait(false))
                        break;

                    await SendBackAsync(buffer.AsMemory(0, len), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]ReadFromTunnel error for {NetUtil.Display(_remoteEp)}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _onClosed?.Invoke(_remoteEp);
            }
        }

        private async Task SendBackAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            _lastActive = DateTime.UtcNow;
            try
            {
                await _udpServer.SendAsync(payload, _remoteEp, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]SendBack error to {NetUtil.Display(_remoteEp)}");
            }
        }

        private async Task MonitorIdleAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token);
                    if (DateTime.UtcNow - _lastActive > SessionIdleTimeout)
                    {
                        _logger.LogDebug($"[UdpForward]Session idle timeout {NetUtil.Display(_remoteEp)}");
                        Dispose();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _idleCts?.Cancel(); } catch { }
            try { _streamCts?.Cancel(); } catch { }
            try { _tunnelStream?.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
        }
    }
}
