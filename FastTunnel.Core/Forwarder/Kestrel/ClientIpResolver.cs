// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FastTunnel.Core.Sockets;

namespace FastTunnel.Core.Forwarder.Kestrel;

/// <summary>
/// 从 L7 请求头还原真实访客 IP，并据此判定访问白名单。
///
/// 安全前提：转发类请求头是访客可以自己伪造的。只有 TCP 对端 IP 落在
/// <c>TrustedProxyIps</c> 里（即请求确实来自我们自己的反向代理）时才采信
/// X-Forwarded-For / Forwarded / X-Real-IP，否则一律以对端 IP 为准。
/// 配错成 <c>0.0.0.0/0</c> 等于把白名单交给访客自己填。
///
/// IPv4 / IPv6 字面量、CIDR、IPv4-mapped IPv6（<c>::ffff:1.2.3.4</c>）统一归一化后比较。
/// </summary>
public static class ClientIpResolver
{
    private static readonly ConditionalWeakTable<string[], IpRuleSet> RuleCache = new();

    /// <summary>解析真实访客 IP。<paramref name="headers"/> 的键不区分大小写。</summary>
    public static IPAddress Resolve(
        IPAddress? remoteAddress,
        IReadOnlyDictionary<string, string>? headers,
        string[]? trustedProxies)
    {
        var peer = NetUtil.Unmap(remoteAddress) ?? IPAddress.None;
        var trusted = RuleSetFor(trustedProxies);

        // 来源不可信：任何转发头都不看，避免访客伪造 XFF 绕过白名单
        if (!trusted.Contains(peer))
        {
            return peer;
        }

        if (headers is { Count: > 0 })
        {
            if (TryGetHeader(headers, "x-forwarded-for", out var xff))
            {
                var picked = PickFromChain(xff.Split(','), trusted);
                if (picked != null)
                {
                    return picked;
                }
            }

            if (TryGetHeader(headers, "forwarded", out var forwarded))
            {
                var picked = PickFromChain(ParseForwardedFor(forwarded), trusted);
                if (picked != null)
                {
                    return picked;
                }
            }

            if (TryGetHeader(headers, "x-real-ip", out var realIp)
                && TryParseAddress(realIp, out var real)
                && !trusted.Contains(real))
            {
                return real;
            }
        }

        return peer;
    }

    /// <summary>访问白名单判定。名单为空表示不限制。</summary>
    public static bool IsAllowed(IPAddress? clientIp, string[]? allowList)
    {
        var rules = RuleSetFor(allowList);
        return rules.IsEmpty || rules.Contains(NetUtil.Unmap(clientIp) ?? IPAddress.None);
    }

    /// <summary>把配置数组编译成规则集。数组按引用缓存，配置热加载换数组即自动失效。</summary>
    private static IpRuleSet RuleSetFor(string[]? values)
        => values is null || values.Length == 0
            ? IpRuleSet.Empty
            : RuleCache.GetValue(values, static array => IpRuleSet.Parse(array));

    /// <summary>取头部，不依赖调用方字典的键比较器。</summary>
    private static bool TryGetHeader(
        IReadOnlyDictionary<string, string> headers, string name, out string value)
    {
        if (headers.TryGetValue(name, out var direct) && direct != null)
        {
            value = direct;
            return true;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// 代理链从右往左找第一个「不在可信代理列表」的地址；全是可信代理时取最左。
    /// 非法项直接跳过，不因为一个脏值就把真实 IP 丢掉。
    /// </summary>
    private static IPAddress? PickFromChain(IReadOnlyList<string> candidates, IpRuleSet trusted)
    {
        // 从右往左遍历，最后一次覆盖到的合法地址就是最左侧那一个
        IPAddress? leftmost = null;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            if (!TryParseAddress(candidates[i], out var address))
            {
                continue;
            }

            leftmost = address;
            if (!trusted.Contains(address))
            {
                return address;
            }
        }

        return leftmost;
    }

    /// <summary>解析 RFC 7239 <c>Forwarded: for=..., for=...</c> 里的 for 参数。</summary>
    private static List<string> ParseForwardedFor(string value)
    {
        var result = new List<string>();
        foreach (var element in value.Split(','))
        {
            foreach (var parameter in element.Split(';'))
            {
                var text = parameter.Trim();
                if (!text.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(text.Substring(4).Trim().Trim('"'));
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析转发头里的单个地址。兼容 <c>1.2.3.4:1234</c>、<c>[2001:db8::1]</c>、
    /// <c>[2001:db8::1]:443</c>、带引号的 RFC 7239 写法，以及 IPv4-mapped IPv6。
    /// </summary>
    public static bool TryParseAddress(string? raw, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        if (text.Length > 0 && text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close <= 1)
            {
                return false;
            }

            text = text.Substring(1, close - 1);
        }
        else
        {
            // 仅当恰好一个冒号时按 IPv4:port 剥掉端口；纯 IPv6 有多个冒号，不能动
            var first = text.IndexOf(':');
            if (first > 0 && first == text.LastIndexOf(':'))
            {
                text = text.Substring(0, first);
            }
        }

        if (!IPAddress.TryParse(text, out var parsed))
        {
            return false;
        }

        address = NetUtil.Unmap(parsed) ?? parsed;
        return true;
    }
}

/// <summary>预编译的 IP / CIDR 规则集，线程安全。</summary>
public sealed class IpRuleSet
{
    public static readonly IpRuleSet Empty = new(Array.Empty<IpRule>());

    private readonly IpRule[] _rules;

    private IpRuleSet(IpRule[] rules) => _rules = rules;

    public bool IsEmpty => _rules.Length == 0;

    public bool Contains(IPAddress? address)
    {
        if (address == null || IsEmpty)
        {
            return false;
        }

        foreach (var rule in _rules)
        {
            if (rule.Matches(address))
            {
                return true;
            }
        }

        return false;
    }

    public static IpRuleSet Parse(string[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return Empty;
        }

        var rules = new List<IpRule>(values.Length);
        foreach (var value in values)
        {
            // 非法配置跳过而不是整体失效，避免一个笔误让服务起不来；启动时另有日志提示
            if (TryParseRule(value, out var rule))
            {
                rules.Add(rule);
            }
        }

        return rules.Count == 0 ? Empty : new IpRuleSet(rules.ToArray());
    }

    private static bool TryParseRule(string? raw, out IpRule rule)
    {
        rule = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        var slash = text.IndexOf('/');
        var hostPart = (slash >= 0 ? text.Substring(0, slash) : text).Trim();

        // 允许 [2001:db8::]/32 这种写法
        if (hostPart.Length > 1 && hostPart[0] == '[' && hostPart[^1] == ']')
        {
            hostPart = hostPart[1..^1];
        }

        if (!IPAddress.TryParse(hostPart, out var ip))
        {
            return false;
        }

        ip = NetUtil.Unmap(ip) ?? ip;
        var maxBits = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxBits;

        if (slash >= 0
            && (!int.TryParse(text.Substring(slash + 1).Trim(), out prefix)
                || prefix < 0 || prefix > maxBits))
        {
            return false;
        }

        rule = new IpRule(ip.GetAddressBytes(), prefix, ip.AddressFamily);
        return true;
    }
}

internal readonly struct IpRule
{
    private readonly byte[] _network;
    private readonly int _prefix;
    private readonly AddressFamily _family;

    public IpRule(byte[] network, int prefix, AddressFamily family)
    {
        _network = network;
        _prefix = prefix;
        _family = family;
    }

    public bool Matches(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != _family || bytes.Length != _network.Length)
        {
            return false;
        }

        var wholeBytes = _prefix / 8;
        for (var i = 0; i < wholeBytes; i++)
        {
            if (bytes[i] != _network[i])
            {
                return false;
            }
        }

        var remainingBits = _prefix % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (bytes[wholeBytes] & mask) == (_network[wholeBytes] & mask);
    }
}
