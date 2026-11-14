// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Net;
using FastTunnel.Core.Forwarder.Kestrel;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 真实访客 IP 还原与访问白名单。这里最关键的安全不变量是：
/// 转发类请求头只在 TCP 对端确实是我们自己的反向代理时才被采信。
/// </summary>
public class ClientIpResolverTests
{
    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    private static IReadOnlyDictionary<string, string> Headers(params (string Name, string Value)[] items)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in items)
        {
            dict[name] = value;
        }

        return dict;
    }

    private static readonly string[] LocalAndIntranetProxies = { "127.0.0.1", "::1", "10.0.0.0/8" };

    [Fact]
    public void 不可信来源伪造XFF时返回对端IP()
    {
        var headers = Headers(("X-Forwarded-For", "10.0.0.1"));

        var result = ClientIpResolver.Resolve(Ip("203.0.113.9"), headers, new[] { "127.0.0.1" });

        Assert.Equal(Ip("203.0.113.9"), result);
    }

    [Fact]
    public void 未配置可信代理时一律忽略转发头()
    {
        var headers = Headers(
            ("X-Forwarded-For", "10.0.0.1"),
            ("X-Real-IP", "10.0.0.2"),
            ("Forwarded", "for=10.0.0.3"));

        var result = ClientIpResolver.Resolve(Ip("203.0.113.9"), headers, Array.Empty<string>());

        Assert.Equal(Ip("203.0.113.9"), result);
    }

    [Fact]
    public void 可信代理下取XFF链最右侧的非可信地址()
    {
        var headers = Headers(("X-Forwarded-For", "198.51.100.7, 10.0.0.2, 10.0.0.3"));

        var result = ClientIpResolver.Resolve(Ip("127.0.0.1"), headers, LocalAndIntranetProxies);

        Assert.Equal(Ip("198.51.100.7"), result);
    }

    [Fact]
    public void XFF链全是可信代理时取最左()
    {
        var headers = Headers(("X-Forwarded-For", "10.0.0.1, 10.0.0.2"));

        var result = ClientIpResolver.Resolve(Ip("127.0.0.1"), headers, LocalAndIntranetProxies);

        Assert.Equal(Ip("10.0.0.1"), result);
    }

    [Fact]
    public void XFF里的脏值不影响解析()
    {
        var headers = Headers(("X-Forwarded-For", "unknown, 198.51.100.7, garbage"));

        var result = ClientIpResolver.Resolve(Ip("127.0.0.1"), headers, LocalAndIntranetProxies);

        Assert.Equal(Ip("198.51.100.7"), result);
    }

    [Fact]
    public void 没有XFF时采信XRealIP()
    {
        var headers = Headers(("X-Real-IP", "198.51.100.8"));

        var result = ClientIpResolver.Resolve(Ip("127.0.0.1"), headers, LocalAndIntranetProxies);

        Assert.Equal(Ip("198.51.100.8"), result);
    }

    [Fact]
    public void 没有XFF时解析RFC7239Forwarded()
    {
        var headers = Headers(("Forwarded", "for=192.0.2.1;proto=https, for=10.0.0.5"));

        var result = ClientIpResolver.Resolve(Ip("127.0.0.1"), headers, LocalAndIntranetProxies);

        Assert.Equal(Ip("192.0.2.1"), result);
    }

    [Fact]
    public void 头部键大小写不敏感且不依赖字典比较器()
    {
        var caseSensitive = new Dictionary<string, string> { ["X-FORWARDED-FOR"] = "198.51.100.9" };

        var result = ClientIpResolver.Resolve(Ip("127.0.0.1"), caseSensitive, LocalAndIntranetProxies);

        Assert.Equal(Ip("198.51.100.9"), result);
    }

    [Fact]
    public void IPv4MappedIPv6对端归一化()
    {
        var result = ClientIpResolver.Resolve(
            Ip("::ffff:203.0.113.9"), null, Array.Empty<string>());

        Assert.Equal(Ip("203.0.113.9"), result);
    }

    [Fact]
    public void XFF里的IPv6带端口可解析()
    {
        var headers = Headers(("X-Forwarded-For", "[2001:db8::1]:443"));

        var result = ClientIpResolver.Resolve(Ip("::1"), headers, new[] { "::1" });

        Assert.Equal(Ip("2001:db8::1"), result);
    }

    [Fact]
    public void 空白名单放行所有来源()
    {
        Assert.True(ClientIpResolver.IsAllowed(Ip("203.0.113.9"), Array.Empty<string>()));
        Assert.True(ClientIpResolver.IsAllowed(null, null));
    }

    [Fact]
    public void 白名单支持IPv4的IP与CIDR()
    {
        var rules = new[] { "192.168.0.101", "10.0.0.0/8" };

        Assert.True(ClientIpResolver.IsAllowed(Ip("192.168.0.101"), rules));
        Assert.True(ClientIpResolver.IsAllowed(Ip("10.12.34.56"), rules));
        Assert.False(ClientIpResolver.IsAllowed(Ip("192.168.0.102"), rules));
        Assert.False(ClientIpResolver.IsAllowed(Ip("203.0.113.9"), rules));
    }

    [Fact]
    public void 白名单支持IPv6CIDR且不与IPv4混判()
    {
        var rules = new[] { "2001:db8::/32" };

        Assert.True(ClientIpResolver.IsAllowed(Ip("2001:db8::1234"), rules));
        Assert.False(ClientIpResolver.IsAllowed(Ip("2001:db9::1"), rules));
        Assert.False(ClientIpResolver.IsAllowed(Ip("127.0.0.1"), rules));
    }

    [Fact]
    public void 伪造XFF不能绕过白名单()
    {
        var headers = Headers(("X-Forwarded-For", "10.0.0.1"));
        var allow = new[] { "10.0.0.0/8" };

        // 对端不是可信代理 → 真实 IP 仍是 203.0.113.9 → 不在白名单
        var clientIp = ClientIpResolver.Resolve(Ip("203.0.113.9"), headers, Array.Empty<string>());

        Assert.False(ClientIpResolver.IsAllowed(clientIp, allow));
    }

    [Fact]
    public void 非法规则项跳过而不让整份配置失效()
    {
        var rules = new[] { "not-an-ip", "192.168.1.0/33", "192.168.1.0/24" };

        Assert.True(ClientIpResolver.IsAllowed(Ip("192.168.1.77"), rules));
        Assert.False(ClientIpResolver.IsAllowed(Ip("192.168.2.77"), rules));
    }

    [Fact]
    public void 零前缀CIDR放行整个地址族()
    {
        var rules = new[] { "0.0.0.0/0" };

        Assert.True(ClientIpResolver.IsAllowed(Ip("203.0.113.9"), rules));
        Assert.False(ClientIpResolver.IsAllowed(Ip("2001:db8::1"), rules));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4:5678", "1.2.3.4")]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("\"1.2.3.4\"", "1.2.3.4")]
    [InlineData("::ffff:1.2.3.4", "1.2.3.4")]
    public void 转发头地址写法兼容(string raw, string expected)
    {
        Assert.True(ClientIpResolver.TryParseAddress(raw, out var parsed));
        Assert.Equal(Ip(expected), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1.2.3.999")]
    [InlineData("[broken")]
    public void 非法转发头地址被拒绝(string raw)
    {
        Assert.False(ClientIpResolver.TryParseAddress(raw, out _));
    }
}
