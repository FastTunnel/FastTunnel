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
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Handlers.Server;
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
    /// 单个 UDP 数据报最大长度（受限于 ushort 的长度前缀以及 IP/UDP 头）
    /// </summary>
    private const int MaxDatagramSize = 65507;

    /// <summary>
    /// 会话空闲超时时间，超过此时间没有数据则关闭会话
    /// </summary>
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger;
    private readonly UdpClient _udpServer;
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
        ListenIp = ip;
        ListenPort = port;

        var localEp = new IPEndPoint(IPAddress.Parse(ListenIp), ListenPort);
        _udpServer = new UdpClient(localEp);
    }

    public void Start()
    {
        _shutdown = false;
        _ = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udpServer.ReceiveAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]ReceiveAsync error on {ListenIp}:{ListenPort}");
                continue;
            }

            var remoteEp = received.RemoteEndPoint;
            var data = received.Buffer;

            try
            {
                var session = _sessions.GetOrAdd(remoteEp, ep => CreateSession(ep));
                await session.SendToTunnelAsync(data, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]Dispatch error from {remoteEp}");
                if (_sessions.TryRemove(remoteEp, out var dead))
                {
                    dead.Dispose();
                }
            }
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

                _logger.LogDebug($"[UdpForward]Session opened for {_remoteEp}");
                _ready.TrySetResult(true);

                _ = Task.Run(() => ReadFromTunnelAsync(_idleCts.Token));
                _ = Task.Run(() => MonitorIdleAsync(_idleCts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]Session open failed for {_remoteEp}");
                _ready.TrySetException(ex);
                Dispose();
            }
        }

        public async Task SendToTunnelAsync(byte[] payload, CancellationToken token)
        {
            if (payload.Length > MaxDatagramSize)
            {
                _logger.LogDebug($"[UdpForward]Drop oversized datagram {payload.Length}");
                return;
            }

            await _ready.Task.WaitAsync(token);

            await _writeLock.WaitAsync(token);
            try
            {
                _lastActive = DateTime.UtcNow;

                // 拼接 [长度 + 负载] 到一个池化缓冲区，只调用一次 WriteAsync，
                // 避免原来两次 await 造成的头部阻塞、以及 .ToArray() 额外分配。
                int frameLen = 2 + payload.Length;
                var frame = ArrayPool<byte>.Shared.Rent(frameLen);
                try
                {
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)payload.Length);
                    if (payload.Length > 0)
                    {
                        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
                    }
                    await _tunnelStream.WriteAsync(frame.AsMemory(0, frameLen), token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frame);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadFromTunnelAsync(CancellationToken token)
        {
            var header = new byte[2];
            var buffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(_tunnelStream, header, 0, 2, token))
                        break;

                    int len = BinaryPrimitives.ReadUInt16BigEndian(header);
                    if (len > MaxDatagramSize) break; // 协议异常，避免越界
                    if (len > 0 && !await ReadExactAsync(_tunnelStream, buffer, 0, len, token))
                        break;

                    _lastActive = DateTime.UtcNow;

                    try
                    {
                        await _udpServer.SendAsync(buffer.AsMemory(0, len), _remoteEp, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, $"[UdpForward]SendBack error to {_remoteEp}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"[UdpForward]ReadFromTunnel error for {_remoteEp}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _onClosed?.Invoke(_remoteEp);
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
                        _logger.LogDebug($"[UdpForward]Session idle timeout {_remoteEp}");
                        Dispose();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, offset + read, count - read, token);
                if (n == 0) return false;
                read += n;
            }
            return true;
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
