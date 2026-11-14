// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using FastTunnel.Core.Handlers.Server;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 域名写入路由表之前的校验。这些用例锁住的是「不可被绕过」的边界：
/// Host 头注入、平台子域抢注、通配符 / URL 混入、IP 字面量。
/// </summary>
public class HostNameValidatorTests
{
    [Theory]
    [InlineData("www.abc.com", "www.abc.com")]
    [InlineData("  WWW.ABC.COM  ", "www.abc.com")]
    [InlineData("www.abc.com.", "www.abc.com")]
    [InlineData("xn--fsq.com", "xn--fsq.com")]
    [InlineData("a-b.example.co.uk", "a-b.example.co.uk")]
    public void 合法域名归一化(string input, string expected)
    {
        Assert.True(HostNameValidator.TryNormalize(input, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("intranet")]
    [InlineData("http://www.abc.com")]
    [InlineData("https://www.abc.com/a?b=1")]
    [InlineData("www.abc.com:1270")]
    [InlineData("www.abc.com/path")]
    [InlineData("*.abc.com")]
    [InlineData(".abc.com")]
    [InlineData("a..b.com")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.0.101")]
    [InlineData("[::1]")]
    [InlineData("-bad.abc.com")]
    [InlineData("bad-.abc.com")]
    [InlineData("foo.local")]
    [InlineData("foo.test")]
    [InlineData("foo.example")]
    [InlineData("foo.invalid")]
    [InlineData("foo.123")]
    [InlineData("www.abc.com\r\nHost: evil.com")]
    [InlineData("www.abc.com bad.com")]
    public void 非法域名一律拒绝(string input)
    {
        Assert.False(HostNameValidator.TryNormalize(input, out var host));
        Assert.Equal(string.Empty, host);
    }

    [Fact]
    public void 超长标签与超长域名拒绝()
    {
        Assert.False(HostNameValidator.TryNormalize(new string('a', 64) + ".com", out _));
        Assert.True(HostNameValidator.TryNormalize(new string('a', 63) + ".com", out _));

        var tooLong = string.Join(".", new[] { new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 63) });
        Assert.False(HostNameValidator.TryNormalize(tooLong, out _));
    }

    [Theory]
    [InlineData("test", true, "test")]
    [InlineData("my-app", true, "my-app")]
    [InlineData("  TEST  ", true, "test")]
    [InlineData("a.b", false, "")]
    [InlineData("test.cc", false, "")]
    [InlineData("test\r\nHost: evil", false, "")]
    [InlineData("-test", false, "")]
    [InlineData("test-", false, "")]
    [InlineData("", false, "")]
    [InlineData("*.cc", false, "")]
    public void 子域名必须是单个DNS标签(string input, bool expected, string normalized)
    {
        Assert.Equal(expected, HostNameValidator.TryNormalizeSubDomain(input, out var label));
        Assert.Equal(normalized, label);
    }

    [Fact]
    public void 平台自有域名及其子域不可作为自定义域名声明()
    {
        Assert.True(HostNameValidator.IsPlatformDomain("test.cc", "TEST.CC."));
        Assert.True(HostNameValidator.IsPlatformDomain("victim.test.cc", "test.cc"));

        Assert.False(HostNameValidator.IsPlatformDomain("nottest.cc", "test.cc"));
        Assert.False(HostNameValidator.IsPlatformDomain("test.cc.evil.com", "test.cc"));
        Assert.False(HostNameValidator.IsPlatformDomain("www.abc.com", null));
        Assert.False(HostNameValidator.IsPlatformDomain("www.abc.com", ""));
    }

    [Fact]
    public void 黑名单支持精确匹配与后缀匹配()
    {
        var blacklist = new[] { "bad.example.com", ".blocked.net", "", "   " };

        Assert.True(HostNameValidator.IsBlacklisted("bad.example.com", blacklist));
        Assert.False(HostNameValidator.IsBlacklisted("sub.bad.example.com", blacklist));

        Assert.True(HostNameValidator.IsBlacklisted("blocked.net", blacklist));
        Assert.True(HostNameValidator.IsBlacklisted("a.b.blocked.net", blacklist));
        Assert.False(HostNameValidator.IsBlacklisted("notblocked.net", blacklist));

        Assert.False(HostNameValidator.IsBlacklisted("good.com", null));
        Assert.False(HostNameValidator.IsBlacklisted("good.com", Array.Empty<string>()));
    }
}
