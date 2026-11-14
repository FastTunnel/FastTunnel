// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using FastTunnel.Core.Config;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 熔断链：立标记 → 关联域名拉黑 → 摘映射 / 停监听 / 关 mux / 断控制面 → 留证 → 待复核。
/// 自动系统不得直接永封，举报不得自动封禁。
/// </summary>
public class BanServiceTests
{
    private sealed class Harness
    {
        public Harness()
        {
            Bans = new BanRegistry(NullLogger<BanRegistry>.Instance, (string?)null);
            Audit = new AuditLog(NullLogger<AuditLog>.Instance, (DefaultServerConfig.AuditOptions?)null);
            Server = new FastTunnelServer(
                NullLogger<FastTunnelServer>.Instance,
                new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig()),
                Bans);

            Client = new TunnelClient(
                new ClientWebSocket(), Server,
                new LoginHandler(NullLogger<LoginHandler>.Instance, Bans),
                IPAddress.Loopback, NullLogger<TunnelClient>.Instance)
            {
                Token = "tok-1",
            };

            Service = new BanService(Server, Bans, Audit, NullLogger<BanService>.Instance);
        }

        public BanRegistry Bans { get; }

        public AuditLog Audit { get; }

        public FastTunnelServer Server { get; }

        public TunnelClient Client { get; }

        public BanService Service { get; }

        public void Register(params string[] hosts)
        {
            foreach (var host in hosts)
            {
                var web = new WebInfo
                {
                    Host = host,
                    Socket = Client.webSocket,
                    WebConfig = new WebConfig { LocalIp = "127.0.0.1", LocalPort = 80 },
                    Client = Client,
                };
                Assert.True(Server.TryRegisterWeb(host, web));
                Client.AddWeb(web);
            }
        }

        public void Login() => Server.OnClientLogin(Client);
    }

    [Fact]
    public void 按域名熔断会拉黑关联域名摘除映射并留证()
    {
        var h = new Harness();
        h.Register("a.test.cc", "b.example.com");
        h.Login();

        var result = h.Service.Ban(new BanCommand
        {
            SubjectType = BanSubjectType.Host,
            Subject = "a.test.cc",
            Reason = "命中违规 URL 情报",
            RuleId = "R-1",
            EvidenceId = "E-1",
            Operator = "moderator",
            ClientIp = "203.0.113.9",
        });

        Assert.True(result.Success);
        Assert.Equal(BanLevel.Block, result.Record!.Level);
        Assert.True(h.Bans.IsHostBanned("a.test.cc"));
        Assert.True(h.Bans.IsHostBanned("b.example.com"), "同会话其它域名必须一并拉黑");
        Assert.Empty(h.Server.WebList);
        Assert.True(h.Client.IsOffline);
        Assert.Contains(h.Audit.Query(action: "ban"), x => x.BanId == result.Record.Id && x.EvidenceId == "E-1");
        Assert.Contains(h.Bans.PendingReviews(), x => x.Id == result.Record.Id);
    }

    [Fact]
    public void 按Token熔断定位到主体并断开其全部映射()
    {
        var h = new Harness();
        h.Register("a.test.cc", "b.example.com");
        h.Login();

        var result = h.Service.Ban(new BanCommand
        {
            SubjectType = BanSubjectType.Token,
            Subject = "tok-1",
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.DisconnectedClients);
        Assert.True(h.Bans.IsTokenBanned("tok-1"));
        Assert.True(h.Bans.IsHostBanned("a.test.cc"));
        Assert.True(h.Bans.IsHostBanned("b.example.com"));
        Assert.Empty(h.Server.WebList);
        Assert.True(h.Client.IsOffline);
    }

    [Fact]
    public void 自动系统请求永封会被降级为熔断()
    {
        var h = new Harness();
        h.Register("a.test.cc");
        h.Login();

        var result = h.Service.Ban(new BanCommand
        {
            SubjectType = BanSubjectType.Host,
            Subject = "a.test.cc",
            RequestedLevel = BanLevel.Permanent,
        });

        Assert.Equal("Permanent", result.DowngradedFrom);
        Assert.Equal(BanLevel.Block, result.Record!.Level);
        Assert.NotEqual(BanLevel.Permanent, h.Bans.Snapshot().Single(x => x.Id == result.Record.Id).Level);
    }

    [Fact]
    public void 人工复核确认后才能升级为永封()
    {
        var h = new Harness();
        h.Register("a.test.cc");
        h.Login();
        var banned = h.Service.Ban(new BanCommand { SubjectType = BanSubjectType.Host, Subject = "a.test.cc" });

        var reviewed = h.Service.Review(banned.Record!.Id, ReviewState.Confirmed, "admin", "确认违法", elevate: true);

        Assert.True(reviewed.Success);
        Assert.Equal(BanLevel.Permanent, reviewed.Record!.Level);
        Assert.Contains(h.Audit.Query(action: "review"), x => x.Operator == "admin");
    }

    [Fact]
    public void 复核驳回视为误封立即整组解封()
    {
        var h = new Harness();
        h.Register("a.test.cc", "b.example.com");
        h.Login();
        var banned = h.Service.Ban(new BanCommand { SubjectType = BanSubjectType.Host, Subject = "a.test.cc" });

        var reviewed = h.Service.Review(banned.Record!.Id, ReviewState.Rejected, "admin", "误报", elevate: false);

        Assert.True(reviewed.Success);
        Assert.False(h.Bans.IsHostBanned("a.test.cc"));
        Assert.False(h.Bans.IsHostBanned("b.example.com"));
    }

    [Fact]
    public void 解封连带移除关联域名()
    {
        var h = new Harness();
        h.Register("a.test.cc", "b.example.com");
        h.Login();
        h.Service.Ban(new BanCommand { SubjectType = BanSubjectType.Host, Subject = "a.test.cc" });

        var result = h.Service.Unban(BanSubjectType.Host, "a.test.cc", "admin", "复核通过");

        Assert.True(result.Success);
        Assert.False(h.Bans.IsHostBanned("a.test.cc"));
        Assert.False(h.Bans.IsHostBanned("b.example.com"));
        Assert.Contains(h.Audit.Query(action: "unban"), x => x.Operator == "admin");
    }

    [Fact]
    public void 举报只进复核队列绝不自动封禁()
    {
        var h = new Harness();
        h.Register("a.test.cc");
        h.Login();

        var id = h.Service.Report(new ReportCommand
        {
            Host = "a.test.cc",
            Path = "/page",
            Reason = "疑似违规内容",
            ClientIp = "203.0.113.9",
        });

        Assert.False(h.Bans.IsHostBanned("a.test.cc"));
        Assert.False(h.Client.IsOffline);
        Assert.Contains(h.Bans.PendingReviews(), x => x.Id == id);
        Assert.Contains(h.Audit.Query(action: "report"), x => x.BanId == id);
    }

    [Fact]
    public void 空主体与不存在的复核对象返回失败()
    {
        var h = new Harness();

        Assert.False(h.Service.Ban(new BanCommand { SubjectType = BanSubjectType.Host, Subject = "  " }).Success);
        Assert.False(h.Service.Unban(BanSubjectType.Host, "none.test.cc", "admin", null).Success);
        Assert.False(h.Service.Review("missing", ReviewState.Confirmed, "admin", null, false).Success);
    }
}
