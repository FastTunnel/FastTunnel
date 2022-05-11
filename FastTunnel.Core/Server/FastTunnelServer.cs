// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Server;

public class FastTunnelServer
{
    public int ConnectedClientCount;
    public readonly IOptionsMonitor<DefaultServerConfig> ServerOption;
    private readonly ILogger<FastTunnelServer> logger;

    public ConcurrentDictionary<string, TaskCompletionSource<IDuplexPipe>> ResponseTasks { get; } = new();

    public ConcurrentDictionary<string, WebInfo> WebList { get; private set; } = new();

    public ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>> ForwardList { get; private set; }
        = new ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>>();

    /// <summary>
    /// 在线客户端列表
    /// </summary>
    public IList<TunnelClient> Clients = new List<TunnelClient>();

    public FastTunnelServer(ILogger<FastTunnelServer> logger, IOptionsMonitor<DefaultServerConfig> serverSettings)
    {
        this.logger = logger;
        ServerOption = serverSettings;
    }

    /// <summary>
    /// 客户端登录
    /// </summary>
    /// <param name="client"></param>
    internal void OnClientLogin(TunnelClient client)
    {
        Interlocked.Increment(ref ConnectedClientCount);
        logger.LogInformation($"客户端连接 {client.RemoteIpAddress} 当前在线数：{ConnectedClientCount}");
        Clients.Add(client);
    }

    /// <summary>
    /// 客户端退出
    /// </summary>
    /// <param name="client"></param>
    /// <exception cref="NotImplementedException"></exception>
    internal void OnClientLogout(TunnelClient client)
    {
        Interlocked.Decrement(ref ConnectedClientCount);
        logger.LogInformation($"客户端关闭  {client.RemoteIpAddress} 当前在线数：{ConnectedClientCount}");
        Clients.Remove(client);
        client.Logout();
    }

    internal bool TryGetWebProxyByHost(string host, out WebInfo web)
    {
        return WebList.TryGetValue(host, out web);
    }
}
