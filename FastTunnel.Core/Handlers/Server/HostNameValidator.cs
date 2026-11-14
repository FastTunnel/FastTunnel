// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace FastTunnel.Core.Handlers.Server;

/// <summary>
/// 客户端声明的域名在写入路由表之前的校验。
///
/// 这一层做的是「语法 + 平台归属边界」校验，不是 DNS 所有权验证：服务端不掌握用户的
/// DNS 账号，无法仅凭一条 LogIn 消息确认 WWW 域名确实归声明者所有。真正的所有权验证
/// （DNS TXT 挑战、ICP 备案查询）属于后续接入流程，见 docs/content-compliance-plan-v0.1.md。
/// 但至少可以杜绝 Host 头注入、平台子域抢注和覆盖他人已登记的映射。
/// </summary>
public static class HostNameValidator
{
    /// <summary>DNS 主机名总长上限（不含末尾点）。</summary>
    public const int MaxHostNameLength = 253;

    /// <summary>单个 DNS 标签的长度上限。</summary>
    public const int MaxLabelLength = 63;

    /// <summary>保留后缀：这些域名不会在公网被正常解析，出现在这里通常是误配或探测。</summary>
    private static readonly string[] ReservedSuffixes =
    {
        ".localhost", ".local", ".localdomain", ".internal", ".test", ".example", ".invalid",
    };

    /// <summary>
    /// 归一化成可直接与 Host 头比较的形式（小写、无末尾点、无端口）。
    /// 只接受 ASCII DNS 标签；中文域名需先自行转成 punycode（xn--）。
    /// </summary>
    public static bool TryNormalize(string? value, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // FQDN 允许以一个点结尾，但浏览器发来的 Host 不会带，统一去掉
        var candidate = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (candidate.Length == 0 || candidate.Length > MaxHostNameLength)
        {
            return false;
        }

        // 只允许 DNS 标签字符集。顺带挡掉 scheme、端口、路径、query、CRLF、通配符和 Unicode 混淆字符。
        foreach (var c in candidate)
        {
            if (!IsValidHostChar(c))
            {
                return false;
            }
        }

        if (candidate[0] == '.' || candidate.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = candidate.Split('.');
        if (labels.Length < 2)
        {
            // 单标签主机名（localhost / intranet）不作为公网域名接受
            return false;
        }

        foreach (var label in labels)
        {
            if (!IsValidLabel(label))
            {
                return false;
            }
        }

        // 顶级域不能是纯数字；IPv4 字面量也不允许当成域名登记
        if (labels[labels.Length - 1].All(char.IsDigit) || IPAddress.TryParse(candidate, out _))
        {
            return false;
        }

        if (ReservedSuffixes.Any(s => candidate.EndsWith(s, StringComparison.Ordinal)))
        {
            return false;
        }

        host = candidate;
        return true;
    }

    /// <summary>
    /// SubDomain 必须是单个 DNS 标签。允许带点就能拼出多级域名，甚至把注入内容带进 Host。
    /// </summary>
    public static bool TryNormalizeSubDomain(string? value, out string label)
    {
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (!IsValidLabel(candidate))
        {
            return false;
        }

        label = candidate;
        return true;
    }

    /// <summary>
    /// WebDomain 及其所有子域都由平台通过 SubDomain 分配，禁止再从 WWW 里声明一次。
    /// 否则可以抢注一个尚无人使用的子域，让原主人登录时反被判成「域名已占用」。
    /// </summary>
    public static bool IsPlatformDomain(string host, string? webDomain)
    {
        if (string.IsNullOrWhiteSpace(webDomain))
        {
            return false;
        }

        var root = webDomain.Trim().TrimEnd('.').ToLowerInvariant();
        return host.Equals(root, StringComparison.Ordinal)
            || host.EndsWith("." + root, StringComparison.Ordinal);
    }

    /// <summary>
    /// 运营侧域名黑名单。支持精确匹配和 <c>.example.com</c>（含其子域）两种写法。
    /// </summary>
    public static bool IsBlacklisted(string host, IEnumerable<string>? blacklist)
    {
        if (blacklist == null)
        {
            return false;
        }

        foreach (var raw in blacklist)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var entry = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry[0] == '.')
            {
                var suffix = entry.Substring(1);
                if (host.Equals(suffix, StringComparison.Ordinal)
                    || host.EndsWith("." + suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (host.Equals(entry, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidLabel(string label)
    {
        if (label.Length == 0 || label.Length > MaxLabelLength)
        {
            return false;
        }

        if (label[0] == '-' || label[label.Length - 1] == '-')
        {
            return false;
        }

        foreach (var c in label)
        {
            if (!IsValidHostChar(c) || c == '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHostChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
}
