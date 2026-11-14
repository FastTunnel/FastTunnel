// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using FastTunnel.Core.Models;

namespace FastTunnel.Core.Server;

/// <summary>
/// 同一域名下的转发成员组，负载均衡的最小单元。
/// <para>
/// 路由表的值由单个 <c>WebInfo</c> 升级为本容器：同组同口令的成员注册进同一个容器
/// （对齐 frp 的 loadBalancer.group 语义），转发时按请求随机选一个存活成员；
/// 无组配置的旧注册是单成员组，行为与旧版覆盖式完全兼容。
/// </para>
/// <para>
/// 组的存在完全由成员注册行为聚合而来，服务端不需要为组做任何配置。
/// 组内成员全部退出时路由条目随之摘除。
/// </para>
/// </summary>
public class WebHostGroup
{
    private readonly object membersLock = new();
    private readonly List<WebInfo> members = new();

    /// <summary>成员健康状态：连续失败计数与摘出池的截止时刻。</summary>
    private readonly Dictionary<WebInfo, MemberHealth> health = new();

    /// <summary>组对应的完整主机名（路由表键）。</summary>
    public string Host { get; }

    /// <summary>组名；null 表示旧版无组注册形成的单成员组。</summary>
    public string? Group { get; }

    /// <summary>组口令；入组校验用，与 <see cref="Group"/> 同时为 null 或同时相等。</summary>
    public string? GroupKey { get; }

    public WebHostGroup(string host, string? group, string? groupKey)
    {
        Host = host;
        Group = group;
        GroupKey = groupKey;
    }

    /// <summary>用现有单条映射构造组（成员入组），兼容测试与迁移场景。</summary>
    public static WebHostGroup ForSingle(WebInfo info)
    {
        var group = new WebHostGroup(info.Host, null, null);
        group.Add(info);
        return group;
    }

    /// <summary>当前成员数（含被摘出池的成员）。</summary>
    public int MemberCount
    {
        get { lock (membersLock) { return members.Count; } }
    }

    /// <summary>成员快照。封禁服务与运维统计用。</summary>
    public WebInfo[] SnapshotMembers()
    {
        lock (membersLock)
        {
            return members.ToArray();
        }
    }

    /// <summary>
    /// 判断一次注册能否并入本组：组名与口令必须一致（null 视为旧版无组）。
    /// 口令不一致一律拒绝，防止不相干客户端混入组劫持流量。
    /// </summary>
    public bool CanJoin(WebInfo info, out string reason)
    {
        // 无主映射来源不明，一律不让并入或覆盖（保留旧版防御）
        if (members.Any(m => m.Client == null))
        {
            reason = "无主映射，来源不明";
            return false;
        }

        var incoming = info.WebConfig?.Group;
        var incomingKey = info.WebConfig?.GroupKey;

        if (Group == null && incoming == null)
        {
            // 旧版无组语义：同域名已被其它在线客户端占用时拒绝；同客户端允许原地刷新，
            // 已离线客户端的残留成员允许被接管（并发窗口由 Logout 清理收敛）。
            if (members.Any(m => !ReferenceEquals(m.Client, info.Client) && m.Client?.IsOffline != true))
            {
                reason = "已被其它客户端占用";
                return false;
            }
        }

        if (!string.Equals(Group ?? null, incoming ?? null, StringComparison.OrdinalIgnoreCase))
        {
            reason = incoming == null
                ? "已被其它客户端占用"
                : "已被其它客户端占用（同名域名归属不同的负载均衡组）";
            return false;
        }

        if (Group != null && !string.Equals(GroupKey, incomingKey, StringComparison.Ordinal))
        {
            reason = "负载均衡组口令不一致，拒绝入组";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 加入成员。同一客户端的重复注册（重连刷新）原地替换，保持旧版语义。
    /// </summary>
    public void Add(WebInfo info)
    {
        lock (membersLock)
        {
            var old = members.FindIndex(m => ReferenceEquals(m.Client, info.Client));
            if (old >= 0)
            {
                health.Remove(members[old]);
                members[old] = info;
            }
            else
            {
                members.Add(info);
            }

            health[info] = new MemberHealth();
        }
    }

    /// <summary>
    /// 移除成员（下线/顶替）。组空返回 true，路由条目可随之摘除。
    /// </summary>
    public bool Remove(WebInfo info)
    {
        lock (membersLock)
        {
            health.Remove(info);
            return members.RemoveAll(m => ReferenceEquals(m, info)) > 0
                && members.Count == 0;
        }
    }

    /// <summary>
    /// 按请求随机选一个成员：优先健康池；全池不健康时仍然随机挑一个
    /// （后端可能已恢复但没有流量可供观测），返回 null 表示组为空。
    /// </summary>
    public WebInfo? PickMember()
    {
        lock (membersLock)
        {
            if (members.Count == 0)
            {
                return null;
            }

            var now = Environment.TickCount64;
            var healthyMembers = members.Where(m =>
                !health.TryGetValue(m, out var h) || h.UnhealthyUntilTicks <= now).ToList();

            var pool = healthyMembers.Count > 0 ? healthyMembers : members;
            return pool[Random.Shared.Next(pool.Count)];
        }
    }

    /// <summary>转发成功（隧道建立）→ 清零失败计数，立即回到健康池。</summary>
    public void ReportSuccess(WebInfo info)
    {
        lock (membersLock)
        {
            if (health.TryGetValue(info, out var h))
            {
                h.ConsecutiveFailures = 0;
                h.UnhealthyUntilTicks = 0;
            }
        }
    }

    /// <summary>
    /// 转发建立失败（连接内网服务被拒/超时、客户端离线）→ 连续失败达到阈值后
    /// 暂时摘出轮询池，<paramref name="recoverySeconds"/> 秒后自动回到候选。
    /// </summary>
    public void ReportFailure(WebInfo info, int failureThreshold, int recoverySeconds)
    {
        lock (membersLock)
        {
            if (!health.TryGetValue(info, out var h))
            {
                if (!members.Any(m => ReferenceEquals(m, info)))
                {
                    return;
                }

                h = new MemberHealth();
                health[info] = h;
            }

            h.ConsecutiveFailures++;
            if (failureThreshold > 0 && h.ConsecutiveFailures >= failureThreshold)
            {
                h.UnhealthyUntilTicks = Environment.TickCount64 + recoverySeconds * 1000L;
                h.ConsecutiveFailures = 0;
            }
        }
    }

    private sealed class MemberHealth
    {
        public int ConsecutiveFailures;
        public long UnhealthyUntilTicks;
    }
}
