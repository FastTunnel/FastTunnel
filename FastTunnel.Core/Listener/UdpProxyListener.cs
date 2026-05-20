// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Handlers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Listener
{
    /// <summary>
    /// UDP public-side listener. Each distinct remote endpoint becomes a
    /// session; the session opens a single tunnel through the FastTunnel
    /// client and exchanges length-prefixed datagrams over it.
    /// </summary>
    public class UdpProxyListener : IPortListener
    {
        // Idle timeout for a UDP session before the underlying tunnel is torn down.
        private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromSeconds(60);

        readonly ILogger _logger;
        readonly WebSocket _client;

        UdpClient _udpClient;
        ForwardDispatcher _requestDispatcher;
        bool _shutdown;

        readonly ConcurrentDictionary<IPEndPoint, UdpSession> _sessions = new();

        public string ListenIp { get; set; }

        public int ListenPort { get; set; }

        public UdpProxyListener(string ip, int port, ILogger logger, WebSocket client)
        {
            _client = client;
            _logger = logger;
            ListenIp = ip;
            ListenPort = port;
        }

        public void Start(ForwardDispatcher requestDispatcher)
        {
            _shutdown = false;
            _requestDispatcher = requestDispatcher;

            var ipa = IPAddress.Parse(ListenIp);
            _udpClient = new UdpClient(new IPEndPoint(ipa, ListenPort));

            _ = Task.Run(ReceiveLoopAsync);
        }

        private async Task ReceiveLoopAsync()
        {
            _logger.LogInformation($"[UDP-Listener:{ListenPort}] started.");
            while (!_shutdown)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udpClient.ReceiveAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"[UDP-Listener:{ListenPort}] receive error: {ex.Message}");
                    if (_shutdown) break;
                    continue;
                }

                var session = _sessions.GetOrAdd(result.RemoteEndPoint, ep => CreateSession(ep));
                session.Enqueue(result.Buffer);
            }
            _logger.LogInformation($"[UDP-Listener:{ListenPort}] stopped.");
        }

        private UdpSession CreateSession(IPEndPoint remoteEp)
        {
            var session = new UdpSession(remoteEp, _udpClient, _requestDispatcher, _client, _logger, SessionIdleTimeout);
            session.Closed += () =>
            {
                _sessions.TryRemove(remoteEp, out _);
                _logger.LogDebug($"[UDP-Listener:{ListenPort}] session closed for {remoteEp}");
            };
            session.Start();
            return session;
        }

        public void Stop()
        {
            if (_shutdown) return;
            _shutdown = true;

            try { _udpClient?.Close(); } catch { }

            foreach (var kv in _sessions)
            {
                try { kv.Value.Stop(); } catch { }
            }
            _sessions.Clear();
        }
    }
}
