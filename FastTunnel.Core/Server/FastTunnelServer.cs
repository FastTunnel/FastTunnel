// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// 待转发列表
    /// </summary>
    public ConcurrentDictionary<Guid, TaskCompletionSource<(Stream Stream, CancellationTokenSource Token)>> ResponseTasks { get; } = new();

    public ConcurrentDictionary<string, WebInfo> WebList { get; private set; } = new();

    public ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>> ForwardList { get; private set; }
        = new ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>>();

    /// <summary>
    /// 在线客户端列表
    /// </summary>
    public IList<TunnelClient> Clients = new List<TunnelClient>();

    /// <summary>
    /// MuxId → 客户端会话。客户端登录时声明 MuxId，随后用同一个 id 建立 MUX 连接，
    /// 服务端靠这张表把匿名 TCP 连接绑定到已认证的登录会话上。
    /// </summary>
    public ConcurrentDictionary<Guid, TunnelClient> MuxClients { get; } = new();

    /// <summary>客户端上下线可能来自不同连接的任务，列表读写需要串行化。</summary>
    private readonly object clientsLock = new();

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
        lock (clientsLock)
        {
            Clients.Add(client);
        }
    }

    /// <summary>
    /// 登录消息解析完成后登记 MuxId（此时才知道客户端声明的会话标识）。
    /// </summary>
    internal void RegisterMuxClient(TunnelClient client)
    {
        if (client.MuxId == Guid.Empty)
        {
            return;
        }

        MuxClients.AddOrUpdate(client.MuxId, client, (_, _) => client);
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
        lock (clientsLock)
        {
            Clients.Remove(client);
        }

        if (client.MuxId != Guid.Empty)
        {
            MuxClients.TryRemove(client.MuxId, out _);
        }

        client.Logout();
    }

    /// <summary>
    /// MUX 连接可能与 LogIn 消息并发到达（客户端拿到登录成功就建连，
    /// 服务端还在处理登录），这里给一个短暂的等待窗口，避免无谓的建连失败。
    /// </summary>
    public async Task<TunnelClient?> WaitMuxClientAsync(Guid muxId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            if (MuxClients.TryGetValue(muxId, out var client))
            {
                return client;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    internal bool TryGetWebProxyByHost(string host, out WebInfo web)
    {
        return WebList.TryGetValue(host, out web);
    }
}
