// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Sockets;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// IPv6 支持的入口。<see cref="NetUtil"/> 里几乎每个方法都对应一处曾经写死 IPv4 的地方，
/// 因此这里逐个覆盖：通配监听、字面量解析、Host 头/URI 的两种写法、双栈连接与地址排序。
/// </summary>
public class NetUtilTests
{
    // ---------------- IsAnyAddress ----------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("*", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("[::]", true)]
    [InlineData(" * ", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("[::1]", false)]
    [InlineData("10.0.0.8", false)]
    public void IsAnyAddress_识别通配写法(string? value, bool expected)
        => Assert.Equal(expected, NetUtil.IsAnyAddress(value));

    // ---------------- ParseListenAddress ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("*")]
    public void ParseListenAddress_通配统一返回IPv6Any(string? value)
        => Assert.Equal(IPAddress.IPv6Any, NetUtil.ParseListenAddress(value));

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("10.0.0.8", "10.0.0.8")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    public void ParseListenAddress_具体地址去掉方括号(string value, string expected)
        => Assert.Equal(IPAddress.Parse(expected), NetUtil.ParseListenAddress(value));

    [Fact]
    public void ParseListenAddress_非法输入抛异常()
        => Assert.ThrowsAny<Exception>(() => NetUtil.ParseListenAddress("not-an-address"));

    // ---------------- HostOnly ----------------

    [Theory]
    [InlineData("[::1]:1270", "::1")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("test.test.cc:1270", "test.test.cc")]
    [InlineData("test.test.cc", "test.test.cc")]
    [InlineData("127.0.0.1:8080", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    // 裸 IPv6 在 Host 头里本来就是非法写法，不做二次破坏
    [InlineData("::1", "::1")]
    [InlineData("", "")]
    public void HostOnly_去掉端口与方括号(string value, string expected)
        => Assert.Equal(expected, NetUtil.HostOnly(value));

    [Fact]
    public void HostOnly_null返回空串()
        => Assert.Equal(string.Empty, NetUtil.HostOnly(null));

    // ---------------- FormatHost ----------------

    [Theory]
    [InlineData("::1", "[::1]")]
    [InlineData("2001:db8::1", "[2001:db8::1]")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("test.test.cc", "test.test.cc")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void FormatHost_IPv6补方括号(string value, string expected)
        => Assert.Equal(expected, NetUtil.FormatHost(value));

    [Fact]
    public void FormatHost_null返回空串()
        => Assert.Equal(string.Empty, NetUtil.FormatHost(null));

    [Theory]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void FormatHost_拼出的URI必须能被解析(string host)
    {
        // 裸 ::1 直接拼会得到 http://::1:1270，Uri 解析直接抛异常
        var uri = $"http://{NetUtil.FormatHost(host)}:1270";

        Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out var parsed));
        Assert.Equal(UriHostNameType.IPv6, parsed!.HostNameType);
    }

    // ---------------- StripBrackets / Unmap / Display ----------------

    [Theory]
    [InlineData("[::1]", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("[]", "")]
    [InlineData("[", "[")]
    [InlineData("test.test.cc", "test.test.cc")]
    public void StripBrackets_只去掉成对方括号(string value, string expected)
        => Assert.Equal(expected, NetUtil.StripBrackets(value));

    [Fact]
    public void Unmap_还原IPv4映射地址()
    {
        var mapped = IPAddress.Parse("::ffff:1.2.3.4");

        Assert.Equal(IPAddress.Parse("1.2.3.4"), NetUtil.Unmap(mapped));
        Assert.Equal(IPAddress.Parse("::1"), NetUtil.Unmap(IPAddress.IPv6Loopback));
        Assert.Null(NetUtil.Unmap(null));
    }

    [Fact]
    public void Display_双栈对端还原成正常写法()
    {
        Assert.Equal("1.2.3.4:80", NetUtil.Display(new IPEndPoint(IPAddress.Parse("::ffff:1.2.3.4"), 80)));
        Assert.Equal("[::1]:80", NetUtil.Display(new IPEndPoint(IPAddress.IPv6Loopback, 80)));
        Assert.Equal("127.0.0.1:80", NetUtil.Display(new IPEndPoint(IPAddress.Loopback, 80)));
        Assert.Equal(string.Empty, NetUtil.Display(null));
    }

    // ---------------- OrderByPreference ----------------

    [Fact]
    public void OrderByPreference_IPv4优先且同族内保持原序()
    {
        var input = new[]
        {
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("10.0.0.2"),
            IPAddress.Parse("::1"),
            IPAddress.Parse("10.0.0.1"),
        };

        var ordered = NetUtil.OrderByPreference(input).ToArray();

        Assert.Equal(IPAddress.Parse("10.0.0.2"), ordered[0]);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), ordered[1]);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), ordered[2]);
        Assert.Equal(IPAddress.Parse("::1"), ordered[3]);
    }

    // ---------------- ResolveAsync ----------------

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData(" 127.0.0.1 ", "127.0.0.1")]
    public async Task ResolveAsync_字面量不走DNS(string host, string expected)
    {
        var addresses = await NetUtil.ResolveAsync(host);

        Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse(expected), addresses[0]);
    }

    // ---------------- BindTcp / BindUdp ----------------

    [Fact]
    public async Task BindTcp_通配地址一个socket同时接v4和v6()
    {
        using var listener = NetUtil.BindTcp(NetUtil.ParseListenAddress("0.0.0.0"), 0);
        listener.Listen(8);

        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using (var v4 = new TcpClient(AddressFamily.InterNetwork))
        {
            await v4.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using var accepted = await listener.AcceptAsync(cts.Token);
            Assert.True(accepted.Connected);
        }

        var dualMode = listener.AddressFamily == AddressFamily.InterNetworkV6 && listener.DualMode;
        if (!dualMode)
        {
            // 系统禁用 IPv6 时回退纯 v4，属预期行为
            return;
        }

        using (var v6 = new TcpClient(AddressFamily.InterNetworkV6))
        {
            await v6.ConnectAsync(IPAddress.IPv6Loopback, port, cts.Token);
            using var accepted = await listener.AcceptAsync(cts.Token);
            Assert.True(accepted.Connected);
        }
    }

    [Fact]
    public async Task BindUdp_通配地址同时接v4和v6数据报()
    {
        using var udp = NetUtil.BindUdp(NetUtil.ParseListenAddress("::"), 0);
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using (var sender = new UdpClient(AddressFamily.InterNetwork))
        {
            await sender.SendAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, port), cts.Token);
            var received = await udp.ReceiveAsync(cts.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, received.Buffer);
        }

        var dualMode = udp.Client.AddressFamily == AddressFamily.InterNetworkV6 && udp.Client.DualMode;
        if (!dualMode)
        {
            return;
        }

        using (var sender6 = new UdpClient(AddressFamily.InterNetworkV6))
        {
            await sender6.SendAsync(new byte[] { 4, 5, 6 }, new IPEndPoint(IPAddress.IPv6Loopback, port), cts.Token);
            var received = await udp.ReceiveAsync(cts.Token);
            Assert.Equal(new byte[] { 4, 5, 6 }, received.Buffer);
        }
    }

    // ---------------- ConnectTcpAsync ----------------

    [Fact]
    public async Task ConnectTcpAsync_连IPv4字面量()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint!).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var accept = listener.AcceptSocketAsync(cts.Token);

            using var socket = await NetUtil.ConnectTcpAsync("127.0.0.1", port, cts.Token);
            using var accepted = await accept;

            Assert.True(socket.Connected);
            Assert.True(accepted.Connected);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectTcpAsync_连IPv6字面量_支持方括号写法()
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            // 环境没有 IPv6 回环，跳过
            return;
        }

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint!).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var accept = listener.AcceptSocketAsync(cts.Token);

            using var socket = await NetUtil.ConnectTcpAsync("[::1]", port, cts.Token);
            using var accepted = await accept;

            Assert.True(socket.Connected);
            Assert.Equal(AddressFamily.InterNetworkV6, socket.AddressFamily);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectTcpAsync_域名解析后能连上()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint!).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var accept = listener.AcceptSocketAsync(cts.Token);

            // localhost 通常同时有 A 和 AAAA，v4 优先策略保证这里连到 v4 监听上
            using var socket = await NetUtil.ConnectTcpAsync("localhost", port, cts.Token);
            using var accepted = await accept;

            Assert.True(socket.Connected);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectTcpAsync_空地址抛参数异常()
        => await Assert.ThrowsAsync<ArgumentException>(() => NetUtil.ConnectTcpAsync("", 80));

    [Fact]
    public async Task ConnectTcpAsync_端口无人监听抛异常()
    {
        // 先占一个端口再释放，拿到一个大概率没人监听的端口号
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint!).Port;
        probe.Stop();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<Exception>(() => NetUtil.ConnectTcpAsync("127.0.0.1", port, cts.Token));
    }
}
