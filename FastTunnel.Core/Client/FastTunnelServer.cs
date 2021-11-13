using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Dispatchers;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.IO;
using Yarp.Sample;
using Yarp.ReverseProxy.Configuration;
using System.Collections.Generic;

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer
    {
        public int ConnectedClientCount = 0;
        public readonly IOptionsMonitor<DefaultServerConfig> ServerOption;
        public IProxyConfigProvider proxyConfig;
        ILogger<FastTunnelServer> logger;

        public ConcurrentDictionary<string, TaskCompletionSource<Stream>> ResponseTasks { get; } = new();

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
    }
}
