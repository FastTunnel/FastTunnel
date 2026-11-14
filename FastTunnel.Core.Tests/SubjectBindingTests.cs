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
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 一户一 token 落地后的主体绑定：域名授权、单会话强制、按 account 熔断。
/// </summary>
public class SubjectBindingTests
{
    private static ClientCredential Named(string id) => ClientCredential.FromOptions(
        new ClientCredentialOptions { Id = id, Token = "tok-" + id })!;

    private static FastTunnelServer Server()
        => new(NullLogger<FastTunnelServer>.Instance,
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig { WebDomain = "test.cc" }));

    private static TunnelClient Client(FastTunnelServer server)
        => new(new StubWebSocket(), server,
            new TestLoginHandler(), IPAddress.Loopback, NullLogger<TunnelClient>.Instance);

    private static LogInMassage Login(string subDomain, params string[] customDomains)
        => new()
        {
            ProtocolVersion = 0,
            Webs = new[]
            {
                new WebConfig
                {
                    SubDomain = subDomain,
                    LocalIp = "127.0.0.1",
                    LocalPort = 80,
                    WWW = customDomains,
                },
            },
        };

    private static ClientCredential Restricted() => ClientCredential.FromOptions(
        new ClientCredentialOptions
        {
            Id = "alice",
            Token = "tok-alice",
            SubDomain = "alice",
            AllowedHosts = new[] { "alice.test.cc", ".example.com" },
        })!;

    [Fact]
    public async Task 主体只能登记授权范围内的域名()
    {
        var server = Server();
        var client = Client(server);
        client.Credential = Restricted();

        await new TestLoginHandler().Run(server, client, Login("alice", "shop.example.com"));

        Assert.True(server.WebList.ContainsKey("alice.test.cc"));
        Assert.True(server.WebList.ContainsKey("shop.example.com"));
    }

    [Fact]
    public async Task 未授权的SubDomain与自定义域名都被跳过()
    {
        var server = Server();
        var client = Client(server);
        client.Credential = Restricted();

        await new TestLoginHandler().Run(server, client, Login("bob", "evil.example.net"));

        Assert.Empty(server.WebList);
    }

    [Fact]
    public async Task 未认证会话沿用原有不限制行为()
    {
        var server = Server();
        var client = Client(server);

        await new TestLoginHandler().Run(server, client, Login("anyone", "www.example.com"));

        Assert.Equal(2, server.WebList.Count);
    }

    [Fact]
    public void 同一具名主体重复登录会原子返回旧会话()
    {
        var server = Server();
        var first = Client(server);
        var second = Client(server);
        first.Credential = Named("alice");
        second.Credential = Named("alice");

        Assert.Null(server.OnClientLogin(first, enforceSingleSession: true));
        Assert.Same(first, server.OnClientLogin(second, enforceSingleSession: true));
        Assert.Equal(2, server.SnapshotClients().Count);
    }

    [Fact]
    public void 旧版共享凭据与不同主体不触发单会话强制()
    {
        var server = Server();
        var legacy1 = Client(server);
        var legacy2 = Client(server);
        legacy1.Credential = ClientCredential.Legacy("shared");
        legacy2.Credential = ClientCredential.Legacy("shared");

        Assert.Null(server.OnClientLogin(legacy1, enforceSingleSession: true));
        Assert.Null(server.OnClientLogin(legacy2, enforceSingleSession: true));

        var bob = Client(server);
        bob.Credential = Named("bob");
        Assert.Null(server.OnClientLogin(bob, enforceSingleSession: true));
    }

    [Fact]
    public void 按account熔断只影响该主体并撤销其全部域名()
    {
        var bans = new BanRegistry(NullLogger<BanRegistry>.Instance, (string?)null);
        var audit = new AuditLog(NullLogger<AuditLog>.Instance, (DefaultServerConfig.AuditOptions?)null);
        var server = new FastTunnelServer(
            NullLogger<FastTunnelServer>.Instance,
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig { WebDomain = "test.cc" }),
            bans);
        var service = new BanService(server, bans, audit, NullLogger<BanService>.Instance);

        var alice = Client(server);
        alice.Credential = Named("alice");
        alice.Token = "tok-alice";
        var web = new WebInfo
        {
            Host = "a.test.cc", Socket = alice.webSocket,
            WebConfig = new WebConfig { LocalIp = "127.0.0.1", LocalPort = 80 }, Client = alice,
        };
        alice.AddWeb(web);
        Assert.True(server.TryRegisterWeb(web.Host, web, out _));

        var bob = Client(server);
        bob.Credential = Named("bob");
        bob.Token = "tok-bob";
        server.OnClientLogin(bob, false);
        server.OnClientLogin(alice, false);

        var result = service.Ban(new BanCommand
        {
            SubjectType = BanSubjectType.Account,
            Subject = "alice",
            Reason = "复核确认",
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.DisconnectedClients);
        Assert.True(bans.IsAccountBanned("alice"));
        Assert.False(bans.IsAccountBanned("bob"));
        Assert.False(server.WebList.ContainsKey("a.test.cc"));
        Assert.True(alice.IsOffline);
        Assert.False(bob.IsOffline);
    }

    private sealed class TestLoginHandler : LoginHandler
    {
        public TestLoginHandler() : base(NullLogger<LoginHandler>.Instance)
        {
        }

        public Task Run(FastTunnelServer server, TunnelClient client, LogInMassage message)
            => HandleLoginAsync(server, client, message, CancellationToken.None);
    }

    /// <summary>不会真正收发的最小 WebSocket：让登录流程里的日志回写能通过。</summary>
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
}
