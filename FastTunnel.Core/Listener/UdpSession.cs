// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Handlers;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FastTunnel.Core.Listener
{
    /// <summary>
    /// Per-remote-endpoint UDP session. It opens a single tunnel stream through
    /// <see cref="ForwardDispatcher"/> and bridges datagrams in both directions
    /// using a 2-byte big-endian length prefix per datagram.
    /// </summary>
    internal sealed class UdpSession
    {
        // Maximum theoretical UDP payload (2-byte length prefix is enough).
        private const int MaxDatagram = 65535;

        public event Action Closed;

        private readonly IPEndPoint _remoteEp;
        private readonly UdpClient _udpClient;
        private readonly ForwardDispatcher _dispatcher;
        private readonly WebSocket _tunnelClient;
        private readonly ILogger _logger;
        private readonly TimeSpan _idleTimeout;

        private readonly Channel<byte[]> _outbound =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        private readonly CancellationTokenSource _cts = new();
        private DateTime _lastActivity = DateTime.UtcNow;

        public UdpSession(IPEndPoint remoteEp, UdpClient udpClient,
            ForwardDispatcher dispatcher, WebSocket tunnelClient,
            ILogger logger, TimeSpan idleTimeout)
        {
            _remoteEp = remoteEp;
            _udpClient = udpClient;
            _dispatcher = dispatcher;
            _tunnelClient = tunnelClient;
            _logger = logger;
            _idleTimeout = idleTimeout;
        }

        public void Start()
        {
            _ = Task.Run(RunAsync);
        }

        public void Enqueue(byte[] datagram)
        {
            _lastActivity = DateTime.UtcNow;
            // DropOldest mode means TryWrite always succeeds for non-completed channels.
            _outbound.Writer.TryWrite(datagram);
        }

        public void Stop()
        {
            try { _outbound.Writer.TryComplete(); } catch { }
            try { _cts.Cancel(); } catch { }
        }

        private async Task RunAsync()
        {
            try
            {
                using var stream = await _dispatcher.RequestUdpTunnelAsync(_tunnelClient, _cts.Token);
                if (stream == null)
                {
                    _logger.LogWarning($"[UDP-Session {_remoteEp}] tunnel not established");
                    return;
                }

                using var idleCts = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, idleCts.Token);

                var sendTask = SendLoopAsync(stream, linked.Token);
                var recvTask = ReceiveLoopAsync(stream, linked.Token);
                var idleTask = MonitorIdleAsync(idleCts, linked.Token);

                await Task.WhenAny(sendTask, recvTask, idleTask);
                idleCts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[UDP-Session {_remoteEp}] error: {ex.Message}");
            }
            finally
            {
                Stop();
                Closed?.Invoke();
            }
        }

        // Public(remote) -> tunnel: read from queue, write [len][payload].
        private async Task SendLoopAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[2];
            try
            {
                while (await _outbound.Reader.WaitToReadAsync(token))
                {
                    while (_outbound.Reader.TryRead(out var data))
                    {
                        if (data.Length == 0 || data.Length > MaxDatagram) continue;
                        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)data.Length);
                        await stream.WriteAsync(header, 0, 2, token);
                        await stream.WriteAsync(data, 0, data.Length, token);
                        await stream.FlushAsync(token);
                        _lastActivity = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug($"[UDP-Session {_remoteEp}] send loop ended: {ex.Message}");
            }
        }

        // Tunnel -> public(remote): read [len][payload], send to UDP client.
        private async Task ReceiveLoopAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[2];
            var pool = ArrayPool<byte>.Shared;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header, 0, 2, token)) break;
                    int len = BinaryPrimitives.ReadUInt16BigEndian(header);
                    if (len == 0) continue;

                    var buffer = pool.Rent(len);
                    try
                    {
                        if (!await ReadExactAsync(stream, buffer, 0, len, token)) break;
                        await _udpClient.SendAsync(buffer, len, _remoteEp);
                        _lastActivity = DateTime.UtcNow;
                    }
                    finally
                    {
                        pool.Return(buffer);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug($"[UDP-Session {_remoteEp}] recv loop ended: {ex.Message}");
            }
        }

        private async Task MonitorIdleAsync(CancellationTokenSource idleCts, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    if (DateTime.UtcNow - _lastActivity > _idleTimeout)
                    {
                        _logger.LogDebug($"[UDP-Session {_remoteEp}] idle timeout, closing");
                        idleCts.Cancel();
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
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
