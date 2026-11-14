// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FastTunnel.Core.Config;
using FastTunnel.Core.Security;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

public class AuditLogTests
{
    private static AuditLog Create(DefaultServerConfig.AuditOptions? options = null)
        => new(NullLogger<AuditLog>.Instance, options);

    [Fact]
    public void 检索倒序分页且可按Host与Action过滤()
    {
        var log = Create();
        log.Write(new AuditEntry { Action = "ban", Host = "a.test.cc", ClientIp = "203.0.113.9" });
        log.Write(new AuditEntry { Action = "report", Host = "b.test.cc" });
        log.Write(new AuditEntry { Action = "blocked_request", Host = "a.test.cc" });

        Assert.Equal("blocked_request", log.Query()[0].Action);
        Assert.Equal(2, log.Query(host: "a.test.cc").Count);
        Assert.Single(log.Query(action: "report"));
        Assert.Equal(2, log.Query(take: 2).Count);
        Assert.Single(log.Query(skip: 2, take: 10));
    }

    [Fact]
    public void 内存窗口只保留最近记录()
    {
        var log = Create(new DefaultServerConfig.AuditOptions { FilePath = "", MemoryCapacity = 100 });
        for (var i = 0; i < 105; i++)
        {
            log.Write(new AuditEntry { Action = "ban", Detail = i.ToString() });
        }

        var all = log.Query(take: 1000);
        Assert.Equal(100, all.Count);
        Assert.Equal("104", all[0].Detail);
        Assert.Equal("5", all[^1].Detail);
    }

    [Fact]
    public void 文件追加为可解析的JSONL()
    {
        using var dir = new TempDirectory();
        var file = dir.Combine("audit.jsonl");
        var log = Create(new DefaultServerConfig.AuditOptions { FilePath = file, MemoryCapacity = 100 });

        log.Write(new AuditEntry { Action = "ban", Host = "a.test.cc", BanId = "id-1" });
        log.Write(new AuditEntry { Action = "review", Host = "a.test.cc", Operator = "admin" });

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.NotNull(JsonSerializer.Deserialize<AuditEntry>(line)));
        Assert.Contains("\"BanId\":\"id-1\"", lines[0]);
    }

    [Fact]
    public void Token指纹不可逆且稳定()
    {
        var a = BanRegistry.Fingerprint("secret-token");
        var b = BanRegistry.Fingerprint("secret-token");

        Assert.Equal(a, b);
        Assert.StartsWith("sha256:", a);
        Assert.DoesNotContain("secret-token", a);
        Assert.Equal(string.Empty, BanRegistry.Fingerprint(null));
    }
}
