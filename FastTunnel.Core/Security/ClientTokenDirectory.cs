// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FastTunnel.Core.Config;
using FastTunnel.Core.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Security;

/// <summary>
/// 一户一 token 的主体目录。把 <c>FastTunnel:Clients[]</c> 编译成「token → 主体」的只读快照，
/// 供控制面登录与 WebSocket MUX 承载共用。旧版共享 <c>Tokens</c> 仍接受，但会标成 legacy。
///
/// 配置热加载：<see cref="IOptionsMonitor{T}.CurrentValue"/> 在 reload 后会换成新实例，
/// 这里按引用比较来重建快照，避免每条请求都重新解析配置。
/// </summary>
public sealed class ClientTokenDirectory
{
    private readonly IOptionsMonitor<DefaultServerConfig> options;
    private readonly ILogger<ClientTokenDirectory> logger;
    private readonly object gate = new();

    private DefaultServerConfig? cachedConfig;
    private Snapshot cachedSnapshot;

    public ClientTokenDirectory(
        ILogger<ClientTokenDirectory> logger, IOptionsMonitor<DefaultServerConfig> options)
    {
        this.logger = logger;
        this.options = options;
        cachedConfig = options.CurrentValue;
        cachedSnapshot = Build(cachedConfig, logger);
    }

    public IReadOnlyList<ClientCredential> Credentials => Current().Credentials;

    public IReadOnlyList<string> LegacyTokens => Current().LegacyTokens;

    public IReadOnlyList<string> Diagnostics => Current().Diagnostics;

    public bool HasNamedClients => Current().Credentials.Count > 0;

    /// <summary>从控制面请求里解析凭据。未配置任何 token 时保持旧的「不校验」行为。</summary>
    public bool TryResolve(HttpContext context, out ClientCredential? credential)
    {
        credential = null;
        var snapshot = Current();
        if (!snapshot.RequireToken)
        {
            return true;
        }

        if (!context.Request.Headers.TryGetValue(ProtocolConst.FASTTUNNEL_TOKEN, out var supplied))
        {
            return false;
        }

        return TryFind(supplied.ToString(), out credential);
    }

    /// <summary>按 token 查找主体。所有比较都是定长的，避免按字符短路造成的时序泄漏。</summary>
    public bool TryFind(string? token, out ClientCredential? credential)
    {
        credential = null;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var snapshot = Current();
        foreach (var item in snapshot.Credentials)
        {
            if (FixedTimeEquals(item.Token, token))
            {
                credential = item;
                return true;
            }
        }

        foreach (var legacy in snapshot.LegacyTokens)
        {
            if (FixedTimeEquals(legacy, token))
            {
                credential = ClientCredential.Legacy(legacy);
                return true;
            }
        }

        return false;
    }

    private Snapshot Current()
    {
        var current = options.CurrentValue;
        var cached = Volatile.Read(ref cachedSnapshot);
        if (ReferenceEquals(Volatile.Read(ref cachedConfig), current))
        {
            return cached;
        }

        lock (gate)
        {
            if (!ReferenceEquals(cachedConfig, current))
            {
                var rebuilt = Build(current, logger);
                Volatile.Write(ref cachedSnapshot, rebuilt);
                cachedConfig = current;
                cached = rebuilt;
            }
            else
            {
                cached = cachedSnapshot;
            }
        }

        return cached;
    }

    private static Snapshot Build(DefaultServerConfig? config, ILogger logger)
    {
        var credentials = new List<ClientCredential>();
        var legacy = new List<string>();
        var diagnostics = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var hasNamedEntries = false;

        foreach (var item in config?.Clients ?? new List<ClientCredentialOptions>())
        {
            if (item == null)
            {
                continue;
            }

            // 配置了 Clients 段就一定要校验 token：即使全部条目都被停用，
            // 也不能悄悄退回「无鉴权」模式，否则停用操作反而变成放行。
            hasNamedEntries = true;

            if (!item.Enabled)
            {
                continue;
            }

            var credential = ClientCredential.FromOptions(item);
            if (credential == null)
            {
                diagnostics.Add("Clients 条目缺少 Id 或 Token，已忽略");
                continue;
            }

            if (!ids.Add(credential.Id))
            {
                diagnostics.Add($"Clients 中主体 Id 重复：{credential.Id}，后一条已忽略");
                continue;
            }

            if (!tokens.Add(credential.Token))
            {
                diagnostics.Add($"Clients 中 Token 重复（主体 {credential.Id}），后一条已忽略");
                continue;
            }

            credentials.Add(credential);
        }

        foreach (var token in config?.Tokens ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!tokens.Add(token))
            {
                diagnostics.Add("旧版 Tokens 中存在与 Clients 重复或自相重复的凭据，已忽略");
                continue;
            }

            legacy.Add(token);
        }

        foreach (var message in diagnostics)
        {
            logger.LogWarning("客户端凭据配置告警：{Message}", message);
        }

        return new Snapshot(
            credentials, legacy, diagnostics,
            hasNamedEntries || credentials.Count > 0 || legacy.Count > 0);
    }

    private static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    internal sealed class Snapshot
    {
        public Snapshot(
            IReadOnlyList<ClientCredential> credentials, IReadOnlyList<string> legacyTokens,
            IReadOnlyList<string> diagnostics, bool requireToken)
        {
            Credentials = credentials;
            LegacyTokens = legacyTokens;
            Diagnostics = diagnostics;
            RequireToken = requireToken;
        }

        public IReadOnlyList<ClientCredential> Credentials { get; }

        public IReadOnlyList<string> LegacyTokens { get; }

        public IReadOnlyList<string> Diagnostics { get; }

        public bool RequireToken { get; }
    }
}
