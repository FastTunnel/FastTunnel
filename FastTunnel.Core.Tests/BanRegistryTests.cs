// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Linq;
using FastTunnel.Core.Security;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>黑名单的规范化、命中、过期、复核与重启恢复。</summary>
public class BanRegistryTests
{
    private static BanRegistry Create(string? stateFile = null)
        => new(NullLogger<BanRegistry>.Instance, stateFile);

    private static BanRecord Record(BanSubjectType type, string subject, BanLevel level = BanLevel.Block)
        => new() { SubjectType = type, Subject = subject, Level = level };

    [Fact]
    public void Token只保存指纹但能稳定命中()
    {
        var registry = Create();
        var record = registry.Add(Record(BanSubjectType.Token, "secret-token"));

        Assert.DoesNotContain("secret-token", record.Subject);
        Assert.StartsWith("sha256:", record.Subject);
        Assert.True(registry.IsTokenBanned("secret-token"));
        Assert.False(registry.IsTokenBanned("another-token"));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Host大小写与尾点归一化()
    {
        var registry = Create();
        registry.Add(Record(BanSubjectType.Host, "WWW.Example.COM"));

        Assert.True(registry.IsHostBanned("www.example.com"));
        Assert.True(registry.IsHostBanned("www.example.com."));
        Assert.True(registry.IsHostBanned("WWW.EXAMPLE.COM"));
        Assert.False(registry.IsHostBanned("other.example.com"));
    }

    [Fact]
    public void Match同时检查Host与Token且Host优先()
    {
        var registry = Create();
        var host = registry.Add(Record(BanSubjectType.Host, "bad.test.cc"));
        registry.Add(Record(BanSubjectType.Token, "tok"));

        Assert.Same(host, registry.Match("bad.test.cc", "tok"));
        Assert.NotNull(registry.Match("unknown.test.cc", "tok"));
        Assert.Null(registry.Match("unknown.test.cc", "other"));
    }

    [Fact]
    public void Observe与过期记录不拦截()
    {
        var registry = Create();
        registry.Add(Record(BanSubjectType.Host, "observe.test.cc", BanLevel.Observe));
        Assert.False(registry.IsHostBanned("observe.test.cc"));

        var expired = Record(BanSubjectType.Host, "expired.test.cc");
        expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        registry.Add(expired);
        Assert.False(registry.IsHostBanned("expired.test.cc"));
    }

    [Fact]
    public void 关联域名按组解封()
    {
        var registry = Create();
        var primary = registry.Add(Record(BanSubjectType.Token, "tok"));
        registry.AddHosts(primary.GroupId, new[] { "a.test.cc", "b.test.cc" }, primary);

        Assert.True(registry.IsHostBanned("a.test.cc"));
        Assert.True(registry.IsHostBanned("b.test.cc"));

        Assert.True(registry.Remove(BanSubjectType.Token, "tok"));
        Assert.False(registry.IsTokenBanned("tok"));
        Assert.False(registry.IsHostBanned("a.test.cc"));
        Assert.False(registry.IsHostBanned("b.test.cc"));
    }

    [Fact]
    public void 复核驳回整组解封确认可升级为永封()
    {
        var registry = Create();
        var primary = registry.Add(Record(BanSubjectType.Host, "a.test.cc"));
        registry.AddHosts(primary.GroupId, new[] { "b.test.cc" }, primary);

        var confirmed = registry.Review(primary.Id, ReviewState.Confirmed, "admin", "确认违规", elevate: true);
        Assert.Equal(BanLevel.Permanent, confirmed!.Level);
        Assert.All(registry.Snapshot(), x => Assert.Equal(ReviewState.Confirmed, x.ReviewState));
        Assert.Empty(registry.PendingReviews());

        var rejected = Create();
        var p2 = rejected.Add(Record(BanSubjectType.Host, "c.test.cc"));
        rejected.AddHosts(p2.GroupId, new[] { "d.test.cc" }, p2);
        rejected.Review(p2.Id, ReviewState.Rejected, "admin", "误封", elevate: false);
        Assert.False(rejected.IsHostBanned("c.test.cc"));
        Assert.False(rejected.IsHostBanned("d.test.cc"));
    }

    [Fact]
    public void 状态文件重启后仍生效且Token指纹不会被二次Hash()
    {
        using var dir = new TempDirectory();
        var file = dir.Combine("bans.json");

        var first = Create(file);
        var primary = first.Add(Record(BanSubjectType.Token, "secret-token"));
        first.AddHosts(primary.GroupId, new[] { "WWW.Example.com" }, primary);
        Assert.True(File.Exists(file));

        var second = Create(file);
        Assert.True(second.IsTokenBanned("secret-token"), "token 指纹不能因重新加载被二次哈希");
        Assert.True(second.IsHostBanned("www.example.com"));
        Assert.Equal(2, second.Snapshot().Count);
        Assert.DoesNotContain(second.Snapshot().Select(x => x.Subject), s => s.Contains("secret-token"));
    }

    [Fact]
    public void 空主体不产生记录()
    {
        var registry = Create();
        Assert.False(registry.IsHostBanned(null));
        Assert.False(registry.IsTokenBanned(string.Empty));
        Assert.Null(registry.Match(null, null));
    }
}
