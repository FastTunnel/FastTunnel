// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 转发登记的端口准入：白名单（AllowPorts）与每客户端端口数上限（MaxPortsPerClient）。
/// 拒绝只跳过该条目并回日志，与域名侧校验行为一致。
/// </summary>
public class ForwardAdmissionTests
{
    private static FastTunnelServer CreateServer(DefaultServerConfig config)
        => new(NullLogger<FastTunnelServer>.Instance, new TestOptionsMonitor<DefaultServerConfig>(config));

    private static TunnelClient CreateClient(FastTunnelServer server)
        => new(new StubWebSocket(), server,
            new LoginHandler(NullLogger<LoginHandler>.Instance),
            IPAddress.Loopback, NullLogger<TunnelClient>.Instance);

    private static TestLoginHandler CreateHandler()
        => new(NullLogger<TestLoginHandler>.Instance);

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class TestLoginHandler : LoginHandler
    {
        public TestLoginHandler(ILogger<TestLoginHandler> logger) : base(logger)
        {
        }

        public Task Run(FastTunnelServer server, TunnelClient client, LogInMassage message)
            => HandleLoginAsync(server, client, message, CancellationToken.None);
    }

    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Text, true));

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken cancellationToken)
            => new(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Text, true));

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task 白名单外的端口被拒绝且不启动监听()
    {
        var config = new DefaultServerConfig
        {
            EnableForward = true,
            AllowPorts = new List<AllowPortOptions> { new() { Start = 20000, End = 21000 } },
        };
        var server = CreateServer(config);
        var client = CreateClient(server);
        var handler = CreateHandler();

        var message = new LogInMassage
        {
            Forwards = new List<ForwardConfig>
            {
                new() { RemotePort = 12345, LocalIp = "127.0.0.1", LocalPort = 80 },
            },
        };

        await handler.Run(server, client, message);

        Assert.Empty(server.ForwardList);
    }

    [Fact]
    public async Task 白名单内的端口正常登记_退出后释放()
    {
        var port = GetFreePort();
        var config = new DefaultServerConfig
        {
            EnableForward = true,
            AllowPorts = new List<AllowPortOptions> { new() { Single = port } },
        };
        var server = CreateServer(config);
        var client = CreateClient(server);
        var handler = CreateHandler();

        try
        {
            var message = new LogInMassage
            {
                Forwards = new List<ForwardConfig>
                {
                    new() { RemotePort = port, LocalIp = "127.0.0.1", LocalPort = 80 },
                },
            };

            await handler.Run(server, client, message);

            Assert.Contains(server.ForwardList, kv => kv.Key == port);
        }
        finally
        {
            client.Logout();
        }

        Assert.DoesNotContain(server.ForwardList, kv => kv.Key == port);
    }

    [Fact]
    public async Task 端口数达到上限后新端口被拒绝()
    {
        var config = new DefaultServerConfig { EnableForward = true, MaxPortsPerClient = 1 };
        var server = CreateServer(config);
        var client = CreateClient(server);
        var handler = CreateHandler();

        // 预置一个已占用的端口，不实际启动监听（不走 LoginHandler，仅注入会话记录）
        client.AddForward(new ForwardInfo<ForwardHandlerArg>
        {
            SSHConfig = new ForwardConfig { RemotePort = 10000 },
        });

        var message = new LogInMassage
        {
            Forwards = new List<ForwardConfig>
            {
                new() { RemotePort = 10001, LocalIp = "127.0.0.1", LocalPort = 80 },
            },
        };

        await handler.Run(server, client, message);

        Assert.Empty(server.ForwardList);
    }

    [Fact]
    public async Task 重新登记同一端口不重复计入上限()
    {
        var port = GetFreePort();
        var config = new DefaultServerConfig { EnableForward = true, MaxPortsPerClient = 1 };
        var server = CreateServer(config);
        var client = CreateClient(server);
        var handler = CreateHandler();

        // 会话里已记录同一端口（旧监听），同端口重新登记属于顶替而非新增
        client.AddForward(new ForwardInfo<ForwardHandlerArg>
        {
            SSHConfig = new ForwardConfig { RemotePort = port },
        });

        try
        {
            var message = new LogInMassage
            {
                Forwards = new List<ForwardConfig>
                {
                    new() { RemotePort = port, LocalIp = "127.0.0.1", LocalPort = 80 },
                },
            };

            await handler.Run(server, client, message);

            Assert.Contains(server.ForwardList, kv => kv.Key == port);
        }
        finally
        {
            client.Logout();
        }
    }

    [Fact]
    public void CountForwardPorts_按端口去重且支持排除()
    {
        var server = CreateServer(new DefaultServerConfig());
        var client = CreateClient(server);

        client.AddForward(new ForwardInfo<ForwardHandlerArg>
        {
            SSHConfig = new ForwardConfig { RemotePort = 10000 },
        });
        client.AddForward(new ForwardInfo<ForwardHandlerArg>
        {
            SSHConfig = new ForwardConfig { RemotePort = 10001 },
        });
        // 同端口第二条记录（登录消息里重复出现也应算一个端口）
        client.AddForward(new ForwardInfo<ForwardHandlerArg>
        {
            SSHConfig = new ForwardConfig { RemotePort = 10001 },
        });

        Assert.Equal(2, client.CountForwardPorts());
        Assert.Equal(1, client.CountForwardPorts(10000));
        Assert.Equal(2, client.CountForwardPorts(12345));
    }
}
