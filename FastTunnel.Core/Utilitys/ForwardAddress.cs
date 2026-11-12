// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net;
using System.Net.Sockets;

namespace FastTunnel.Core.Utilitys
{
    /// <summary>
    /// 隧道指令里 "host:port" 目标地址的统一拼法。
    /// </summary>
    public static class ForwardAddress
    {
        /// <summary>
        /// URI 的主机段：裸 IPv6 必须加区号，否则 Uri 解析失败。
        /// </summary>
        public static string UriHost(string host)
        {
            if (host.Contains(':')
                && IPAddress.TryParse(host, out var ip)
                && ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return $"[{host}]";
            }

            return host;
        }

        /// <summary>
        /// IPv6 地址加区号（[::1]:8080），否则端口数字会与地址末段混在一起无法解析。
        /// </summary>
        public static string Format(string host, int port)
        {
            if (IPAddress.TryParse(host, out var ip)
                && ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return $"[{host}]:{port}";
            }

            return $"{host}:{port}";
        }
    }
}
