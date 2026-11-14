// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Models;
using FastTunnel.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Server;

public class FastTunnelServer
{
    public int ConnectedClientCount;
    public readonly IOptionsMonitor<DefaultServerConfig> ServerOption;
    private readonly ILogger<FastTunnelServer> logger;

    /// <summary>封禁黑名单。协议解析要用它判断「映射已摘除但 Host 仍被封」的请求。</summary>
    public BanRegistry? Bans { get; }

    /// <summary>按代理的流量计量与限速。端口转发调度器也通过它挂装饰器。</summary>
    public TrafficMeter? Traffic { get; }

    /// <summary>
    /// 待转发列表
    /// </summary>
    public ConcurrentDictionary<Guid, TaskCompletionSource<(Stream Stream, CancellationTokenSource Token)>> ResponseTasks { get; } = new();

    /// <summary>
    /// 域名路由表：host → 转发成员组。同组同口令的成员聚合进同一容器实现负载均衡，
    /// 无组配置的注册是单成员组，行为与旧版一致。
    /// </summary>
    public ConcurrentDictionary<string, WebHostGroup> WebList { get; private set; } = new();

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

    public FastTunnelServer(
        ILogger<FastTunnelServer> logger, IOptionsMonitor<DefaultServerConfig> serverSettings,
        BanRegistry? bans = null, TrafficMeter? traffic = null)
    {
        this.logger = logger;
        ServerOption = serverSettings;
        Bans = bans;
        Traffic = traffic;
    }

    /// <summary>
    /// Host 是否已被封禁。熔断会摘掉 WebList 映射，此时 FastTunelProtocol 靠这个方法
    /// 仍然生成 Feature，GuardMiddleware 才能返回 451 而不是退化成 404。
    /// </summary>
    public bool IsHostBanned(string? host) => Bans?.IsHostBanned(host) ?? false;

    /// <summary>在线会话快照。封禁按 token / host 反查主体时用，避免直接读写非线程安全的列表。</summary>
    public IReadOnlyList<TunnelClient> SnapshotClients()
    {
        lock (clientsLock)
        {
            return Clients.ToArray();
        }
    }

    /// <summary>
    /// 强制断开一个客户端：摘掉 MuxClients 绑定，Logout 清理 WebList / ForwardList /
    /// 监听与 mux 会话，最后 Abort 控制面。活动转发连接会随 CTS 一并取消。
    /// </summary>
    /// <param name="client">要断开的会话。</param>
    /// <param name="reason">写进日志的断链原因（违规熔断 / 同主体重复登录）。</param>
    public void TerminateClient(TunnelClient client, string reason = "违规熔断")
    {
        if (client.MuxId != Guid.Empty)
        {
            MuxClients.TryRemove(new KeyValuePair<Guid, TunnelClient>(client.MuxId, client));
        }

        try
        {
            client.Logout();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "熔断清理客户端会话时异常 {RemoteIp}", client.RemoteIpAddress);
        }

        try
        {
            client.webSocket.Abort();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "熔断断开控制面时异常 {RemoteIp}", client.RemoteIpAddress);
        }

        logger.LogWarning("已强制断开客户端 {RemoteIp}（{Reason}）", client.RemoteIpAddress, reason);
    }

    /// <summary>
    /// 登记一条在线会话。
    /// <para>
    /// 一户一 token 下发 <paramref name="enforceSingleSession"/> 时，在 clientsLock 内原子地
    /// 找出同主体的旧会话并返回给调用方去断开——两个并发登录不可能同时都认为自己是唯一会话。
    /// 断链放在锁外执行，避免 Abort 触发的回调重入这把锁。
    /// </para>
    /// </summary>
    /// <returns>被顶掉的旧会话；没有则为 null。</returns>
    internal TunnelClient? OnClientLogin(TunnelClient client, bool enforceSingleSession = false)
    {
        TunnelClient? displaced = null;
        lock (clientsLock)
        {
            if (enforceSingleSession && client.HasNamedCredential)
            {
                displaced = Clients.FirstOrDefault(x =>
                    !x.IsOffline
                    && !ReferenceEquals(x, client)
                    && string.Equals(x.Subject, client.Subject, StringComparison.OrdinalIgnoreCase));
            }

            Clients.Add(client);
        }

        Interlocked.Increment(ref ConnectedClientCount);
        logger.LogInformation("客户端连接 {RemoteIp}（主体 {Subject}）当前在线数：{Count}",
            client.RemoteIpAddress, client.Subject, ConnectedClientCount);
        return displaced;
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

    /// <summary>全部在线映射的成员快照。封禁按 client 反查域名用。</summary>
    public WebInfo[] SnapshotWebs()
    {
        return WebList.Values.SelectMany(g => g.SnapshotMembers()).ToArray();
    }

    internal bool TryGetWebProxyByHost(string host, out WebInfo web)
    {
        web = null!;
        return WebList.TryGetValue(host, out var group)
            && (web = group.PickMember()!) != null;
    }

    /// <summary>
    /// 登记一条域名映射。三种情况：
    /// <list type="bullet">
    /// <item>同组同口令且入口一致 → 并入组做负载均衡（frp loadBalancer.group 语义）；</item>
    /// <item>同客户端重复注册（重连刷新）→ 原地替换成员；</item>
    /// <item>其余冲突（组名/口令不一致、旧版无组的他人在线占用）→ 拒绝，
    /// 杜绝通过 WWW 声明覆盖他人域名或混入别人的组。</item>
    /// </list>
    /// </summary>
    internal bool TryRegisterWeb(string host, WebInfo info, out string reason)
    {
        while (true)
        {
            if (!WebList.TryGetValue(host, out var group))
            {
                var incoming = info.WebConfig?.Group;
                var created = new WebHostGroup(host, incoming, info.WebConfig?.GroupKey);
                if (WebList.TryAdd(host, created))
                {
                    created.Add(info);
                    info.GroupRef = created;
                    reason = string.Empty;
                    return true;
                }

                // 并发下别的线程先建了组，重走合并逻辑
                continue;
            }

            if (!group.CanJoin(info, out reason))
            {
                return false;
            }

            group.Add(info);
            info.GroupRef = group;
            reason = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// 摘除指定成员。按 (host, 实例) 一起比较，避免旧客户端下线时误删同名域名的新归属；
    /// 组内最后一个成员退出时路由条目一并摘除。
    /// 封禁服务也可复用该方法立即撤销路由。
    /// </summary>
    internal void RemoveWeb(WebInfo info)
    {
        if (string.IsNullOrEmpty(info.Host))
        {
            return;
        }

        if (!WebList.TryGetValue(info.Host, out var group))
        {
            return;
        }

        if (group.Remove(info))
        {
            WebList.TryRemove(new KeyValuePair<string, WebHostGroup>(info.Host, group));
        }
    }

    /// <summary>会话下线时一并摘除端口转发记录；监听已由 <see cref="TunnelClient"/> 停止。</summary>
    internal void RemoveForward(ForwardInfo<ForwardHandlerArg> info)
    {
        var port = info.SSHConfig?.RemotePort ?? 0;
        if (port <= 0)
        {
            return;
        }

        ForwardList.TryRemove(new KeyValuePair<int, ForwardInfo<ForwardHandlerArg>>(port, info));
    }
}
