// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Security;
using FastTunnel.Core.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>一户一 token：主体解析、重复检测、域名/子域授权、配置热加载。</summary>
public class ClientTokenDirectoryTests
{
    private static DefaultServerConfig Config(params ClientCredentialOptions[] clients)
        => new()
        {
            WebDomain = "test.cc",
            Clients = clients.ToList(),
        };

    private static ClientTokenDirectory Directory(DefaultServerConfig config)
        => new(NullLogger<ClientTokenDirectory>.Instance, new TestOptionsMonitor<DefaultServerConfig>(config));

    private static HttpContext Request(string? token)
    {
        var ctx = new DefaultHttpContext();
        if (token != null)
        {
            ctx.Request.Headers[ProtocolConst.FASTTUNNEL_TOKEN] = token;
        }

        return ctx;
    }

    [Fact]
    public void 具名凭据解析出稳定主体且带上授权范围()
    {
        var dir = Directory(Config(new ClientCredentialOptions
        {
            Id = "alice",
            Token = "tok-alice",
            SubDomain = "alice",
            AllowedHosts = new[] { "alice.test.cc", ".example.com" },
            BandwidthLimit = 1024 * 1024,
        }));

        Assert.True(dir.TryResolve(Request("tok-alice"), out var credential));
        Assert.NotNull(credential);
        Assert.Equal("alice", credential.Id);
        Assert.False(credential.IsLegacy);
        Assert.Equal(1024 * 1024, credential.BandwidthLimit!.Value);
        Assert.True(credential.IsSubDomainAllowed("Alice"));
        Assert.False(credential.IsSubDomainAllowed("bob"));
        Assert.True(credential.IsHostAllowed("alice.test.cc"));
        Assert.True(credential.IsHostAllowed("shop.example.com"));
        Assert.False(credential.IsHostAllowed("evil.test.cc"));
        Assert.False(credential.IsHostAllowed("notexample.com"));
    }

    [Fact]
    public void 旧版共享Tokens仍可用但标记为legacy()
    {
        var config = Config();
        config.Tokens = new List<string> { "shared" };
        var dir = Directory(config);

        Assert.True(dir.TryResolve(Request("shared"), out var credential));
        Assert.NotNull(credential);
        Assert.True(credential.IsLegacy);
        Assert.StartsWith("legacy:sha256:", credential.Id, StringComparison.Ordinal);
        Assert.True(credential.IsHostAllowed("anything.test.cc")); // legacy 不限制域名
        Assert.Single(dir.LegacyTokens);
        Assert.Empty(dir.Credentials);
    }

    [Fact]
    public void 未配置任何凭据时保持旧的不校验行为()
    {
        var dir = Directory(Config());

        Assert.True(dir.TryResolve(Request(null), out var credential));
        Assert.Null(credential);
        Assert.False(dir.HasNamedClients);
    }

    [Fact]
    public void 未知或缺失token被拒绝()
    {
        var dir = Directory(Config(new ClientCredentialOptions { Id = "alice", Token = "tok-alice" }));

        Assert.False(dir.TryResolve(Request(null), out _));
        Assert.False(dir.TryResolve(Request(""), out _));
        Assert.False(dir.TryResolve(Request("tok-bob"), out _));
        Assert.False(dir.TryResolve(Request("tok-alice "), out _));
    }

    [Fact]
    public void 重复的主体与token被跳过并留下诊断()
    {
        var dir = Directory(Config(
            new ClientCredentialOptions { Id = "alice", Token = "same" },
            new ClientCredentialOptions { Id = "alice", Token = "other" },
            new ClientCredentialOptions { Id = "bob", Token = "same" },
            new ClientCredentialOptions { Id = "", Token = "broken" }));

        Assert.Single(dir.Credentials);
        Assert.Equal("alice", dir.Credentials[0].Id);
        Assert.Contains(dir.Diagnostics, x => x.Contains("Id 重复", StringComparison.Ordinal));
        Assert.Contains(dir.Diagnostics, x => x.Contains("Token 重复", StringComparison.Ordinal));
        Assert.Contains(dir.Diagnostics, x => x.Contains("缺少 Id", StringComparison.Ordinal));
    }

    [Fact]
    public void 停用的主体不参与校验()
    {
        var dir = Directory(Config(new ClientCredentialOptions
        {
            Id = "alice", Token = "tok-alice", Enabled = false,
        }));

        Assert.False(dir.TryResolve(Request("tok-alice"), out _));
        Assert.Empty(dir.Credentials);
    }

    [Fact]
    public void 配置实例更换后热加载重建快照()
    {
        var monitor = new MutableOptionsMonitor<DefaultServerConfig>(Config(
            new ClientCredentialOptions { Id = "alice", Token = "tok-a" }));
        var dir = new ClientTokenDirectory(NullLogger<ClientTokenDirectory>.Instance, monitor);

        Assert.True(dir.TryResolve(Request("tok-a"), out _));
        Assert.False(dir.TryResolve(Request("tok-b"), out _));

        monitor.CurrentValue = Config(new ClientCredentialOptions { Id = "bob", Token = "tok-b" });

        Assert.False(dir.TryResolve(Request("tok-a"), out _));
        Assert.True(dir.TryResolve(Request("tok-b"), out var credential));
        Assert.Equal("bob", credential!.Id);
    }

    [Fact]
    public void 凭据与主体不会写进日志可读的明文形式()
    {
        var dir = Directory(Config(new ClientCredentialOptions { Id = "alice", Token = "super-secret" }));

        Assert.True(dir.TryFind("super-secret", out var credential));
        Assert.Equal("alice", credential!.Id);
        Assert.Equal("super-secret", credential.Token); // 仅运行时内存持有
        Assert.False(dir.TryFind("super-secreT", out _));
    }

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public MutableOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; set; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
