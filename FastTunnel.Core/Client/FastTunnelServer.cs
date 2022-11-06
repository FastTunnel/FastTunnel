// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using System.IO;
using Yarp.ReverseProxy.Configuration;
using System.Collections.Generic;
using FastTunnel.Core.Forwarder.MiddleWare;

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer
    {
        public int ConnectedClientCount;
        public readonly IOptionsMonitor<DefaultServerConfig> ServerOption;
        public IProxyConfigProvider proxyConfig;
        readonly ILogger<FastTunnelServer> logger;

        public ConcurrentDictionary<string, (TaskCompletionSource<Stream>, CancellationToken)> ResponseTasks { get; } = new();

        public ConcurrentDictionary<string, WebInfo> WebList { get; private set; } = new();

        public ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>> ForwardList { get; private set; }
            = new ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>>();

        /// <summary>
        /// 在线客户端列表
        /// </summary>
        public IList<TunnelClient> Clients = new List<TunnelClient>();

        public FastTunnelServer(ILogger<FastTunnelServer> logger, IProxyConfigProvider proxyConfig, IOptionsMonitor<DefaultServerConfig> serverSettings)
        {
            this.logger = logger;
            this.ServerOption = serverSettings;
            this.proxyConfig = proxyConfig;
        }

        /// <summary>
        /// 客户端登录
        /// </summary>
        /// <param name="client"></param>
        internal void ClientLogin(TunnelClient client)
        {
            Interlocked.Increment(ref ConnectedClientCount);
            logger.LogInformation($"客户端连接 {client.RemoteIpAddress} 当前在线数：{ConnectedClientCount}，统计CLIENT连接数：{FastTunnelClientHandler.ConnectionCount}");
            Clients.Add(client);
        }

        /// <summary>
        /// 客户端退出
        /// </summary>
        /// <param name="client"></param>
        /// <exception cref="NotImplementedException"></exception>
        internal void ClientLogout(TunnelClient client)
        {
            Interlocked.Decrement(ref ConnectedClientCount);
            logger.LogInformation($"客户端关闭  {client.RemoteIpAddress} 当前在线数：{ConnectedClientCount}，统计CLIENT连接数：{FastTunnelClientHandler.ConnectionCount}");
            Clients.Remove(client);
            client.Logout();
        }
    }
}
