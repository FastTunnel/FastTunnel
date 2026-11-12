// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Client.Sockets
{

    public class DnsSocketFactory
    {
        public static async Task<Socket> ConnectAsync(string host, int port)
        {
            var Socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            // DualMode 让同一个 socket 既能连 AAAA 也能连 A 记录（IPv4 走 IPv4-mapped IPv6）。
            Socket.DualMode = true;
            var dnsEndPoint = new DnsEndPoint(host, port);
            await Socket.ConnectAsync(dnsEndPoint);
            return Socket;
        }

        public static async Task<Socket> ConnectAsync(string hostPort)
        {
            SplitHostPort(hostPort, out var host, out var port);
            return await ConnectAsync(host, port);
        }

        /// <summary>
        /// 按目标地址族创建 UDP socket；host 为域名时同时解析 A/AAAA，
        /// 优先使用与本机网络匹配的地址族（纯 IPv6 机器上拿不到 IPv4 地址）。
        /// </summary>
        public static async Task<UdpClient> CreateUdpClientAsync(string host, int port)
        {
            IPAddress target;
            if (IPAddress.TryParse(host, out var literal))
            {
                target = literal;
            }
            else
            {
                var entries = await Dns.GetHostAddressesAsync(host);
                if (entries.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                target = entries.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                    ?? entries[0];
            }

            var udp = target.AddressFamily == AddressFamily.InterNetworkV6
                ? new UdpClient(AddressFamily.InterNetworkV6)
                : new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(new IPEndPoint(target, port));
            return udp;
        }

        /// <summary>
        /// 解析 "host:port"。IPv6 目标应写成带区号的 "[addr]:port"（服务端 Format 已如此发送）；
        /// 无区号时若整串本身是合法 IPv6 地址（如 "::1"、"fe80::1:2:3"），末段数字属于地址而非端口。
        /// </summary>
        public static void SplitHostPort(string address, out string host, out int port)
        {
            port = 0;
            host = address ?? string.Empty;

            if (string.IsNullOrEmpty(address))
            {
                return;
            }

            if (address[0] == '[')
            {
                var close = address.IndexOf(']');
                if (close >= 0)
                {
                    host = address.Substring(1, close - 1);
                    var rest = address.Substring(close + 1);
                    if (rest.StartsWith(":"))
                    {
                        int.TryParse(rest.Substring(1), out port);
                    }
                    return;
                }
            }

            var idx = address.LastIndexOf(':');
            if (idx < 0)
            {
                return;
            }

            var portPart = address.Substring(idx + 1);
            // 末段必须是 1-9 位纯数字才可能是端口；否则（如 "::1" 的空末段）冒号属于 IPv6 地址本身。
            if (portPart.Length == 0 || portPart.Length > 9 || portPart.Any(c => !char.IsDigit(c)))
            {
                return;
            }

            var head = address.Substring(0, idx);
            // 无区号且整串仍是合法 IPv6 地址时（"::1"、"fe80::1:2:3"），末段数字属于地址而非端口。
            if (IPAddress.TryParse(address, out var whole)
                && whole.AddressFamily == AddressFamily.InterNetworkV6
                && (head.Length == 0 || head.Contains(':')))
            {
                return;
            }

            port = int.Parse(portPart);
            host = head;
        }
    }

}
