// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using FastTunnel.Core.Config;

namespace FastTunnel.Core.Security;

/// <summary>
/// 校验通过后的客户端主体。<see cref="Id"/> 是封禁、审计与带宽统计的归属键，
/// <see cref="Token"/> 只用于与 BanRegistry 的 token 指纹比对，不进日志。
/// </summary>
public sealed class ClientCredential
{
    private readonly string[] allowedHosts;

    private ClientCredential(
        string id, string token, bool isLegacy, string? subDomain,
        string[] allowedHosts, long? bandwidthLimit)
    {
        Id = id;
        Token = token;
        IsLegacy = isLegacy;
        SubDomain = string.IsNullOrWhiteSpace(subDomain) ? null : subDomain.Trim().ToLowerInvariant();
        this.allowedHosts = allowedHosts;
        BandwidthLimit = bandwidthLimit;
    }

    public string Id { get; }

    public string Token { get; }

    /// <summary>来自旧版共享 <c>Tokens</c> 的凭据。这类凭据不提供可靠主体身份，也不参与单会话强制。</summary>
    public bool IsLegacy { get; }

    public string? SubDomain { get; }

    public long? BandwidthLimit { get; }

    public IReadOnlyList<string> AllowedHosts => allowedHosts;

    public bool HasHostRestrictions => allowedHosts.Length > 0;

    public bool IsSubDomainAllowed(string? label)
        => SubDomain == null
           || (label != null && string.Equals(SubDomain, label.Trim().ToLowerInvariant(), StringComparison.Ordinal));

    /// <summary>主机名白名单：精确匹配，或 <c>.suffix</c> 形式含子域。</summary>
    public bool IsHostAllowed(string? host)
    {
        if (allowedHosts.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var candidate = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var entry in allowedHosts)
        {
            if (entry.Length > 0 && entry[0] == '.')
            {
                var suffix = entry.Substring(1);
                if (candidate.Equals(suffix, StringComparison.Ordinal)
                    || candidate.EndsWith("." + suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (candidate.Equals(entry, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>配置条目 → 运行时凭据。返回 null 表示条目非法，应跳过并告警。</summary>
    public static ClientCredential? FromOptions(ClientCredentialOptions options)
    {
        if (options == null
            || string.IsNullOrWhiteSpace(options.Id)
            || string.IsNullOrWhiteSpace(options.Token))
        {
            return null;
        }

        var hosts = (options.AllowedHosts ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ClientCredential(
            options.Id.Trim(), options.Token, isLegacy: false, options.SubDomain,
            hosts, options.BandwidthLimit);
    }

    /// <summary>
    /// 入网设备凭据。台账只保存 token 指纹，明文由请求侧带回来，
    /// 让封禁、审计与带宽统计拿到和配置文件条目一样的主体。
    /// </summary>
    public static ClientCredential Enrolled(string id, string token)
        => new(id, token, isLegacy: false, null, Array.Empty<string>(), null);

    /// <summary>
    /// 兼容旧版共享 Tokens：无法区分人，只能把 token 指纹当主体。
    /// 保留这条路径是为了不中断既有部署，但按 token 封禁仍会误伤所有共用者。
    /// </summary>
    public static ClientCredential Legacy(string token)
        => new("legacy:" + BanRegistry.Fingerprint(token), token, isLegacy: true,
            null, Array.Empty<string>(), null);
}
