// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets;

/// <summary>
/// 双栈（IPv4 + IPv6）相关的统一入口。
///
/// 隧道里最容易踩的两类坑都收敛在这里：
/// 1. 监听：以前一律 <c>AddressFamily.InterNetwork</c>，IPv6 用户连不上；现在「任意地址」
///    用 <c>[::]</c> + <c>DualMode</c>，一个 socket 同时接两种客户端（系统禁用 IPv6 时自动退回 v4）；
/// 2. 连接/拼串：IPv6 字面量必须写成 <c>[::1]</c>。Host 头、URI、日志三处的写法不同，
///    混用就会得到 <c>http://::1:1270</c> 这种非法串，或把 <c>[::1]:1270</c> 截成 <c>[</c>。
/// </summary>
public static class NetUtil
{
    /// <summary>是否「监听所有地址」的写法：0.0.0.0 / :: / * / 空。</summary>
    public static bool IsAnyAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        return text is "*" or "0.0.0.0" or "::" or "[::]";
    }

    /// <summary>
    /// 解析监听地址。通配写法统一返回 <see cref="IPAddress.IPv6Any"/>，
    /// 由 <see cref="BindTcp"/> / <see cref="BindUdp"/> 决定是否双栈。
    /// </summary>
    public static IPAddress ParseListenAddress(string? value)
    {
        if (IsAnyAddress(value))
        {
            return IPAddress.IPv6Any;
        }

        return IPAddress.Parse(StripBrackets(value!.Trim()));
    }

    /// <summary>
    /// 绑定 TCP 监听。IPv6Any 走双栈（一个 socket 收 v4+v6），失败则退回 IPv4。
    /// </summary>
    public static Socket BindTcp(IPAddress address, int port)
    {
        if (address.Equals(IPAddress.IPv6Any))
        {
            try
            {
                var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // DualMode 必须在 Bind 之前设置，否则 Windows 默认 V6ONLY=1，v4 客户端连不上
                    dual.DualMode = true;
                    dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    return dual;
                }
                catch
                {
                    dual.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException
                                           or NotSupportedException or ArgumentException)
            {
                // 系统禁用 IPv6（容器常见）：退回纯 IPv4，不影响原有能力
            }
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }

    /// <summary>绑定 UDP 监听，双栈策略同 <see cref="BindTcp"/>。</summary>
    public static UdpClient BindUdp(IPAddress address, int port)
    {
        if (address.Equals(IPAddress.IPv6Any))
        {
            try
            {
                var dual = new UdpClient(AddressFamily.InterNetworkV6);
                try
                {
                    dual.Client.DualMode = true;
                    dual.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    return dual;
                }
                catch
                {
                    dual.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException
                                           or NotSupportedException or ArgumentException)
            {
                // 同上：IPv6 不可用时退回 IPv4
            }
        }

        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return udp;
    }

    /// <summary>
    /// 连接目标（域名 / IPv4 / IPv6 字面量都支持）。
    ///
    /// 有多个地址时 IPv4 优先：隧道客户端最常见的故障是「DNS 给了 AAAA，但本机没有可用
    /// IPv6 出口」，v6 先试会卡在 SYN 重传上十几秒；纯 IPv6 目标（只有 AAAA）自然走 v6。
    /// </summary>
    public static async Task<Socket> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("目标地址为空", nameof(host));
        }

        var addresses = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        Exception? last = null;

        foreach (var address in OrderByPreference(addresses))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>解析主机名；字面量直接返回，不查 DNS。</summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        var literal = StripBrackets(host.Trim());
        if (IPAddress.TryParse(literal, out var address))
        {
            return new[] { address };
        }

        return await Dns.GetHostAddressesAsync(literal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>IPv4 优先、IPv6 兜底的排序（保持同族内的原有顺序）。</summary>
    public static IEnumerable<IPAddress> OrderByPreference(IEnumerable<IPAddress> addresses)
        => addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

    /// <summary>
    /// <c>[::1]:1270</c> → <c>::1</c>；<c>test.test.cc:1270</c> → <c>test.test.cc</c>。
    /// 裸 IPv6（无方括号）原样返回——它在 Host 头里本来就是非法写法，不做二次破坏。
    /// </summary>
    public static string HostOnly(string? hostPort)
    {
        if (string.IsNullOrEmpty(hostPort))
        {
            return hostPort ?? string.Empty;
        }

        if (hostPort[0] == '[')
        {
            var close = hostPort.IndexOf(']');
            return close > 0 ? hostPort.Substring(1, close - 1) : StripBrackets(hostPort);
        }

        var first = hostPort.IndexOf(':');
        if (first > 0 && first == hostPort.LastIndexOf(':'))
        {
            return hostPort.Substring(0, first);
        }

        return hostPort;
    }

    /// <summary>
    /// 拼 URI / Host 头用的主机部分：<c>::1</c> → <c>[::1]</c>。
    /// 不加方括号会得到 <c>http://::1:1270</c>，Uri 解析直接抛异常。
    /// </summary>
    public static string FormatHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return host ?? string.Empty;
        }

        if (host[0] == '[')
        {
            return host;
        }

        return host.Contains(':') ? $"[{host}]" : host;
    }

    /// <summary>去掉包裹 IPv6 的方括号。</summary>
    public static string StripBrackets(string value)
        => value.Length > 1 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;

    /// <summary>
    /// 双栈 socket 上拿到的 IPv4 对端是 <c>::ffff:1.2.3.4</c>，转回正常写法用于日志与展示。
    /// </summary>
    public static IPAddress? Unmap(IPAddress? address)
        => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>
    /// 日志用：双栈 socket 上收到的 IPv4 对端会显示为 <c>::ffff:1.2.3.4</c>，还原成正常写法。
    /// 仅用于展示，不要拿它的结果去回发——回发必须用原始（已映射）端点。
    /// </summary>
    public static string Display(EndPoint? endPoint)
    {
        if (endPoint is IPEndPoint ip)
        {
            return ip.Address.IsIPv4MappedToIPv6
                ? $"{ip.Address.MapToIPv4()}:{ip.Port}"
                : $"{FormatHost(ip.Address.ToString())}:{ip.Port}";
        }

        return endPoint?.ToString() ?? string.Empty;
    }
}
