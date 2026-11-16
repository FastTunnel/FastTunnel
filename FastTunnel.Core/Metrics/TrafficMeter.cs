// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Metrics;

/// <summary>
/// 按代理的流量计量 + 带宽限速。
///
/// 设计要点：不改数据面的搬运逻辑，只在「写入目标」外包一层装饰器
/// （<see cref="MeteredPipeWriter"/> / <see cref="MeteredStream"/>）。
/// 这样 <c>PipeReader.CopyToAsync</c> 的零拷贝路径保持不变，计量和限速都发生在
/// 每一次写入之前，四条数据面（HTTP 回连 / HTTP mux / TCP 转发回连 / TCP 转发 mux）统一覆盖。
///
/// 限速是软限速：令牌不足时延迟写入，靠 Pipe 的背压把压力传回发送方；
/// 连接关闭会通过取消令牌立即中断等待，不会残留挂起任务。
/// </summary>
public sealed class TrafficMeter
{
    private readonly IOptionsMonitor<DefaultServerConfig> options;
    private readonly ILogger<TrafficMeter> logger;
    private readonly ConcurrentDictionary<string, ProxyCounters> proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucket> buckets = new(StringComparer.Ordinal);
    private readonly object rulesGate = new();

    private DefaultServerConfig? cachedConfig;
    private BandwidthRuleSet? cachedRules;
    private readonly long startedTicks = Environment.TickCount64;

    private long totalBytesIn;
    private long totalBytesOut;
    private long requests;
    private long rejectedBanned;
    private long rejectedAllowlist;
    private long activeConnections;
    private long throttleTicks;

    public TrafficMeter(
        ILogger<TrafficMeter> logger, IOptionsMonitor<DefaultServerConfig> options)
    {
        this.logger = logger;
        this.options = options;
    }

    public double UptimeSeconds => (Environment.TickCount64 - startedTicks) / 1000.0;

    /// <summary>包一层计量后的 PipeWriter，用于 PipeReader.CopyToAsync(PipeWriter) 的目标端。</summary>
    public PipeWriter Wrap(PipeWriter inner, TrafficScope scope, TrafficDirection direction)
        => new MeteredPipeWriter(inner, this, Counters(scope), scope, direction);

    /// <summary>包一层计量后的 Stream，用于 PipeReader.CopyToAsync(Stream) / Stream.CopyToAsync(Stream) 的目标端。</summary>
    public Stream Wrap(Stream inner, TrafficScope scope, TrafficDirection direction)
        => new MeteredStream(inner, this, Counters(scope), scope, direction);

    /// <summary>
    /// UDP 端口转发用的双向流：写入算一个方向、读取算另一个方向。
    /// <paramref name="leaveOpen"/> 为 false 时由装饰器接管内层流的释放。
    /// </summary>
    public Stream WrapBidirectional(
        Stream inner, TrafficScope scope,
        TrafficDirection writeDirection, TrafficDirection readDirection, bool leaveOpen = true)
        => new MeteredStream(inner, this, Counters(scope), scope, writeDirection, readDirection, leaveOpen);

    public void RecordRequest() => Interlocked.Increment(ref requests);

    public void RecordRejection(bool banned)
    {
        if (banned)
        {
            Interlocked.Increment(ref rejectedBanned);
        }
        else
        {
            Interlocked.Increment(ref rejectedAllowlist);
        }
    }

    public void ConnectionOpened(TrafficScope scope)
    {
        Interlocked.Increment(ref activeConnections);
        Counters(scope).OpenConnection();
    }

    public void ConnectionClosed(TrafficScope scope)
    {
        Interlocked.Decrement(ref activeConnections);
        Counters(scope).CloseConnection();
    }

    /// <summary>
    /// 记一次写入并按需限速。由两个装饰器在把数据交给内层之前调用。
    ///
    /// 这条路径每次写入都会走，所以「不限速」必须是最便宜的分支：
    /// 不做字符串闭包分配、不对空规则表做字典查找、不动令牌桶的锁。
    /// 真正的限速等待被拆到 <see cref="ThrottleAsync"/>，只有配置了速率才会进入。
    /// </summary>
    internal ValueTask AccountAsync(
        TrafficScope scope, TrafficDirection direction, int bytes, CancellationToken cancellationToken)
        => AccountAsync(scope, Counters(scope), direction, bytes, cancellationToken);

    /// <summary>
    /// 装饰器专用的重载：代理计数器在创建时就查好并缓存，热路径不再每次做字典查找和字符串比较。
    /// </summary>
    internal ValueTask AccountAsync(
        TrafficScope scope, ProxyCounters counters, TrafficDirection direction,
        int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return default;
        }

        if (direction == TrafficDirection.In)
        {
            Interlocked.Add(ref counters.BytesIn, bytes);
            Interlocked.Add(ref totalBytesIn, bytes);
        }
        else
        {
            Interlocked.Add(ref counters.BytesOut, bytes);
            Interlocked.Add(ref totalBytesOut, bytes);
        }

        var rules = Rules();
        var rate = ResolveRate(rules, scope);

        if (rate <= 0)
        {
            if (Volatile.Read(ref counters.HasLimit) != 0)
            {
                Volatile.Write(ref counters.HasLimit, 0);
                counters.SetLimit(0);
            }

            return default;
        }

        if (Volatile.Read(ref counters.HasLimit) == 0)
        {
            Volatile.Write(ref counters.HasLimit, 1);
        }

        counters.SetLimit(rate);

        return ThrottleAsync(scope, counters, direction, bytes, rules, rate, cancellationToken);
    }

    private async ValueTask ThrottleAsync(
        TrafficScope scope, ProxyCounters counters, TrafficDirection direction,
        int bytes, BandwidthRuleSet rules, long rate, CancellationToken cancellationToken)
    {
        var capacity = Math.Max(1.0, rate * Math.Max(0.05, rules.BurstSeconds));
        var bucket = buckets.GetOrAdd(
            scope.Proxy + "|" + (int)direction,
            _ => new TokenBucket(rate, capacity));

        // 配置热加载只改速率：Configure 的快路径只做两次比较，
        // 但每次写入都要进一次锁，所以合并进 Reserve 一次拿锁完成。
        var delay = bucket.Reserve(rate, capacity, bytes);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        Interlocked.Add(ref throttleTicks, delay.Ticks);
        Interlocked.Add(ref counters.ThrottleTicks, delay.Ticks);

        // 单次等待上限 1 小时：极端错误配置下也不至于让连接永久挂着，
        // 而连接关闭会通过 cancellationToken 立刻唤醒。
        var capped = delay.TotalMilliseconds > 3_600_000
            ? TimeSpan.FromMilliseconds(3_600_000)
            : delay;

        if (capped > TimeSpan.FromSeconds(1))
        {
            logger.LogTrace("代理 {Proxy} 方向 {Direction} 限速等待 {Delay}ms",
                scope.Proxy, direction, (long)capped.TotalMilliseconds);
        }

        await Task.Delay(capped, cancellationToken).ConfigureAwait(false);
    }

    public TrafficSnapshot Snapshot()
    {
        var rows = proxies.Values
            .Select(x => x.ToSnapshot())
            .OrderByDescending(x => x.BytesTotal)
            .ThenBy(x => x.Proxy, StringComparer.Ordinal)
            .ToArray();

        return new TrafficSnapshot
        {
            UptimeSeconds = UptimeSeconds,
            TotalBytesIn = Interlocked.Read(ref totalBytesIn),
            TotalBytesOut = Interlocked.Read(ref totalBytesOut),
            RequestsTotal = Interlocked.Read(ref requests),
            RejectedBanned = Interlocked.Read(ref rejectedBanned),
            RejectedAllowlist = Interlocked.Read(ref rejectedAllowlist),
            ActiveConnections = Interlocked.Read(ref activeConnections),
            ThrottleDelaySeconds = Interlocked.Read(ref throttleTicks) / (double)TimeSpan.TicksPerSecond,
            Proxies = rows,
        };
    }

    private ProxyCounters Counters(TrafficScope scope)
    {
        // static 委托：避免每次写入都因为捕获 scope 而生成一个闭包对象
        var counters = proxies.GetOrAdd(scope.Proxy, static key => new ProxyCounters(key, "anonymous"));
        counters.NoteClient(scope.Client);
        return counters;
    }

    private static long ResolveRate(BandwidthRuleSet rules, TrafficScope scope)
    {
        // 默认部署这三张表都是空的：先看 Count 再查字典，避免每条写入都算一遍字符串哈希
        if (rules.PerProxy.Count > 0 && rules.PerProxy.TryGetValue(scope.Proxy, out var perProxy))
        {
            return Math.Max(0, perProxy);
        }

        if (rules.PerClient.Count > 0 && rules.PerClient.TryGetValue(scope.Client, out var perClient))
        {
            return Math.Max(0, perClient);
        }

        if (scope.ClientLimit.HasValue)
        {
            return Math.Max(0, scope.ClientLimit.Value);
        }

        return Math.Max(0, rules.Default);
    }

    private BandwidthRuleSet Rules()
    {
        var current = options.CurrentValue;
        var cached = Volatile.Read(ref cachedRules);
        if (cached != null && ReferenceEquals(Volatile.Read(ref cachedConfig), current))
        {
            return cached;
        }

        lock (rulesGate)
        {
            if (cachedRules == null || !ReferenceEquals(cachedConfig, current))
            {
                cached = BandwidthRuleSet.Build(current?.Bandwidth);
                Volatile.Write(ref cachedRules, cached);
                cachedConfig = current;
            }
            else
            {
                cached = cachedRules;
            }
        }

        return cached!;
    }

    internal sealed class ProxyCounters
    {
        private string client;
        private long active;

        public ProxyCounters(string proxy, string client)
        {
            Proxy = proxy;
            this.client = client;
            FirstSeenUtc = DateTimeOffset.UtcNow;
        }

        public string Proxy { get; }

        public long BytesIn;
        public long BytesOut;
        public long ConnectionsTotal;
        public long ThrottleTicks;
        public long LimitBytesPerSecond;
        public int HasLimit;

        public DateTimeOffset FirstSeenUtc { get; }

        /// <summary>只在主体真的变化时才写：这条路径每次写入都会走，避免无谓的易失写。</summary>
        public void NoteClient(string value)
        {
            if (!string.Equals(Volatile.Read(ref client), value, StringComparison.Ordinal))
            {
                Volatile.Write(ref client, value);
            }
        }

        public void OpenConnection()
        {
            Interlocked.Increment(ref ConnectionsTotal);
            Interlocked.Increment(ref active);
        }

        public void CloseConnection() => Interlocked.Decrement(ref active);

        public void SetLimit(long rate) => Interlocked.Exchange(ref LimitBytesPerSecond, rate);

        public ProxyTrafficSnapshot ToSnapshot() => new()
        {
            Proxy = Proxy,
            Client = Volatile.Read(ref client),
            BytesIn = Interlocked.Read(ref BytesIn),
            BytesOut = Interlocked.Read(ref BytesOut),
            ConnectionsTotal = Interlocked.Read(ref ConnectionsTotal),
            ActiveConnections = Interlocked.Read(ref active),
            ThrottleDelaySeconds = Interlocked.Read(ref ThrottleTicks) / (double)TimeSpan.TicksPerSecond,
            LimitBytesPerSecond = Volatile.Read(ref HasLimit) == 1
                ? Interlocked.Read(ref LimitBytesPerSecond)
                : 0,
            FirstSeenUtc = FirstSeenUtc,
        };
    }

    internal sealed class BandwidthRuleSet
    {
        public long Default;
        public double BurstSeconds = 1.0;
        public Dictionary<string, long> PerProxy = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> PerClient = new(StringComparer.OrdinalIgnoreCase);

        public static BandwidthRuleSet Build(BandwidthOptions? source)
        {
            var rules = new BandwidthRuleSet();
            if (source == null)
            {
                return rules;
            }

            rules.Default = source.DefaultBytesPerSecond;
            rules.BurstSeconds = source.BurstSeconds > 0 ? source.BurstSeconds : 1.0;
            rules.PerProxy = Normalize(source.PerProxy);
            rules.PerClient = Normalize(source.PerClient);
            return rules;
        }

        private static Dictionary<string, long> Normalize(Dictionary<string, long>? source)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (var pair in source)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    result[pair.Key.Trim().ToLowerInvariant()] = pair.Value;
                }
            }

            return result;
        }
    }
}
