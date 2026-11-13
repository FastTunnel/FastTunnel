// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets;

/// <summary>
/// 客户端连接内网目标 / 服务端的入口。双栈解析与回退逻辑见 <see cref="NetUtil.ConnectTcpAsync"/>。
/// </summary>
public class DnsSocketFactory
{
    public static Task<Socket> ConnectAsync(string host, int port)
        => ConnectAsync(host, port, CancellationToken.None);

    public static Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        => NetUtil.ConnectTcpAsync(host, port, cancellationToken);
}
