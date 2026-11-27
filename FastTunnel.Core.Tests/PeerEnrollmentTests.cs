// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Security;
using FastTunnel.Core.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>设备入网：入网码签名、单次兑换、台账持久化与主体目录接线。</summary>
public class PeerEnrollmentTests
{
    private const string Key = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static PeerRegistry Registry(string? stateFile = null, int maxPeers = 0)
        => new(NullLogger<PeerRegistry>.Instance, stateFile, maxPeers);

    private static PeerCredential Enroll(PeerRegistry registry, string name, string hostname = "lab-pc")
    {
        var issued = registry.CreateTicket(Key, name, 15);
        var credential = registry.Redeem(Key, issued.Code, hostname);
        Assert.NotNull(credential);
        return credential!;
    }

    [Fact]
    public void 入网码自包含签名与截止时间()
    {
        var ticket = Guid.NewGuid();
        var code = PeerEnrollment.Create(Key, ticket, 15);
        Assert.StartsWith("fte1.", code, StringComparison.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var read = PeerEnrollment.Read(code, Key, now);
        Assert.NotNull(read);
        Assert.Equal(ticket, read!.Value.Ticket);
        Assert.InRange(read.Value.Expires - now, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fte1.deadbeef")]
    [InlineData("ftpi1.0f1e2d3c4b5a69788796a5b4c3d2e1f0.1700000000.AABB")]
    public void 非法入网码一律拒绝(string? code)
        => Assert.Null(PeerEnrollment.Read(code, Key, DateTimeOffset.UtcNow));

    [Fact]
    public void 换密钥或改一个字符都验不过()
    {
        var ticket = Guid.NewGuid();
        var code = PeerEnrollment.Create(Key, ticket, 15);
        var other = new string('a', 64);

        Assert.Null(PeerEnrollment.Read(code, other, DateTimeOffset.UtcNow));
        Assert.Null(PeerEnrollment.Read(code[..^1] + (code[^1] == 'A' ? 'B' : 'A'), Key, DateTimeOffset.UtcNow));
        Assert.Null(PeerEnrollment.Read(code, Key, DateTimeOffset.UtcNow.AddMinutes(20)));
    }

    [Fact]
    public void 密钥不是六十四个十六进制字符时拒绝签发()
        => Assert.Throws<ArgumentException>(() => PeerEnrollment.Create("short-key", Guid.NewGuid(), 15));

    [Fact]
    public void 兑换一次即作废并产出长期凭据()
    {
        var registry = Registry();
        var issued = registry.CreateTicket(Key, "studio", 15);

        var credential = registry.Redeem(Key, issued.Code, "studio-mac");
        Assert.NotNull(credential);
        Assert.Equal("studio", credential.Id);
        Assert.Matches("^[0-9a-f]{64}$", credential.Token);
        Assert.Single(registry.Peers);

        Assert.Null(registry.Redeem(Key, issued.Code, "studio-mac"));
        Assert.Empty(registry.PendingTickets());
    }

    [Fact]
    public void 未配置的密钥与空的入网码都兑换不了()
    {
        var registry = Registry();
        var issued = registry.CreateTicket(Key, "studio", 15);

        Assert.Null(registry.Redeem(new string('b', 64), issued.Code, "host"));
        Assert.Null(registry.Redeem(Key, null, "host"));
        Assert.Null(registry.Redeem(Key, "   ", "host"));
        Assert.Single(registry.PendingTickets());
    }

    [Fact]
    public void 台账只存指纹不存明文凭据()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ft-peers-{Guid.NewGuid():N}.json");
        try
        {
            var registry = Registry(file);
            var issued = registry.CreateTicket(Key, "nas", 15);
            var credential = registry.Redeem(Key, issued.Code, "nas-box")!;

            var text = File.ReadAllText(file);
            Assert.DoesNotContain(credential.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(issued.Code, text, StringComparison.Ordinal);
            Assert.Contains("sha256:", text, StringComparison.Ordinal);
            Assert.Contains("\"nas\"", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void 重启后台账与待用码都恢复()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ft-peers-{Guid.NewGuid():N}.json");
        try
        {
            var first = Registry(file);
            var credential = Enroll(first, "desk");
            var kept = first.CreateTicket(Key, "spare", 15);

            var second = Registry(file);
            Assert.Equal("desk", Assert.Single(second.Peers).Id);
            Assert.Equal(kept.Id, Assert.Single(second.PendingTickets()).Id);
            Assert.NotNull(second.Redeem(Key, kept.Code, "spare-box"));
            Assert.True(second.Current.TryFind(credential.Token, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void 吊销后凭据立刻失效恢复后重新可用()
    {
        var registry = Registry();
        var credential = Enroll(registry, "laptop");

        Assert.True(registry.Current.TryFind(credential.Token, out var id));
        Assert.Equal("laptop", id);

        Assert.True(registry.Revoke("laptop"));
        Assert.False(registry.Current.TryFind(credential.Token, out _));
        Assert.False(registry.Revoke("laptop"));

        Assert.True(registry.Restore("laptop"));
        Assert.True(registry.Current.TryFind(credential.Token, out _));
        Assert.True(registry.Remove("laptop"));
        Assert.Empty(registry.Peers);
        Assert.False(registry.Remove("laptop"));
    }

    [Fact]
    public void 设备数达到上限后拒绝继续兑换()
    {
        var registry = Registry(maxPeers: 1);
        Enroll(registry, "one");

        var second = registry.CreateTicket(Key, "two", 15);
        Assert.Null(registry.Redeem(Key, second.Code, "two-box"));
        Assert.Equal("one", Assert.Single(registry.Peers).Id);
    }

    [Fact]
    public void 同名设备自动编号且主机名参与命名()
    {
        var registry = Registry();
        Assert.Equal("home", Enroll(registry, "Home").Id);
        Assert.Equal("home-2", Enroll(registry, "home").Id);
        Assert.Equal("lab-pc", Enroll(registry, null, "Lab-PC").Id);
        Assert.True(registry.Peers.All(x => PeerProtocol.IsName(x.Id)));
    }

    [Fact]
    public void 主机名里的非法字符收敛成连字符()
    {
        var registry = Registry();
        var credential = Enroll(registry, null, "../../etc passwd");

        Assert.True(PeerProtocol.IsName(credential.Id));
        Assert.DoesNotContain("/", credential.Id, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", credential.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void 外部改过台账文件后刷新即生效()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ft-peers-{Guid.NewGuid():N}.json");
        try
        {
            var registry = Registry(file);
            var credential = Enroll(registry, "desk");
            Assert.True(registry.Current.TryFind(credential.Token, out _));

            // 模拟管理员直接用编辑器吊销设备：内存快照不变，刷新后跟文件对齐
            var stored = System.Text.Json.JsonSerializer.Deserialize<PeerRegistryFile>(
                File.ReadAllText(file), AppJsonContext.Default.Options)!;
            stored.Peers[0].Enabled = false;
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(stored, AppJsonContext.Default.Options));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));

            Assert.True(registry.Current.TryFind(credential.Token, out _));
            registry.RefreshFromDisk();
            Assert.False(Assert.Single(registry.Peers).Enabled);
            Assert.False(registry.Current.TryFind(credential.Token, out _));

            // 文件没再变化时重复刷新是空操作
            registry.RefreshFromDisk();
            Assert.False(Assert.Single(registry.Peers).Enabled);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void 主体目录认出已入网设备且不影响匿名放行()
    {
        var registry = Registry();
        var credential = Enroll(registry, "kiosk");
        var dir = new ClientTokenDirectory(
            NullLogger<ClientTokenDirectory>.Instance,
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig()),
            registry);

        Assert.True(dir.TryResolve(Request(credential.Token), out var resolved));
        Assert.NotNull(resolved);
        Assert.Equal("kiosk", resolved.Id);
        Assert.False(resolved.IsLegacy);
        Assert.True(dir.HasNamedClients);

        // 没配任何凭据时，不带 token 的连接依旧按旧行为放行
        Assert.True(dir.TryResolve(Request(null), out var anonymous));
        Assert.Null(anonymous);

        // 台账变更后快照要换新，否则吊销不会生效
        registry.Revoke("kiosk");
        Assert.False(dir.TryFind(credential.Token, out _));
    }

    [Fact]
    public void 记录上线时间不影响凭据判定()
    {
        var registry = Registry();
        var credential = Enroll(registry, "phone");
        registry.Touch("phone");

        Assert.NotNull(registry.LastSeenOf("phone"));
        Assert.Null(registry.LastSeenOf("unknown"));
        registry.Touch(null);
        Assert.True(registry.Current.TryFind(credential.Token, out _));
    }

    private static HttpContext Request(string? token)
    {
        var ctx = new DefaultHttpContext();
        if (token != null)
        {
            ctx.Request.Headers[ProtocolConst.FASTTUNNEL_TOKEN] = token;
        }

        return ctx;
    }
}
