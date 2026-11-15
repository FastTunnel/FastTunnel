// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Diagnostics;

namespace FastTunnel.Core.Metrics;

/// <summary>
/// 令牌桶。允许短时突发，长期平均速率不超过 <c>rate</c> 字节/秒。
///
/// 采用「可负债」策略：本次写入令牌不够时记成负余额并返回需要等待的时长，
/// 下一次调用先按经过时间补充再结算，因此不需要为每个连接单独排队。
/// 同一 proxy+direction 的所有连接共用一个桶，限速是「按代理」而不是「按连接」。
/// </summary>
internal sealed class TokenBucket
{
    private readonly object gate = new();
    private double rate;
    private double capacity;
    private double tokens;
    private long lastTicks;

    public TokenBucket(double rate, double capacity)
    {
        this.rate = rate;
        this.capacity = capacity;
        tokens = capacity;
        lastTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>配置热加载：速率变化时先按旧速率结算，再切换，避免重置导致瞬间放大流量。</summary>
    public void Configure(double newRate, double newCapacity)
    {
        lock (gate)
        {
            Reconfigure(newRate, newCapacity);
        }
    }

    /// <summary>
    /// 预占 bytes 个字节，返回需要等待的时间；余额充足时返回 <see cref="TimeSpan.Zero"/>。
    ///
    /// 速率与容量一起传进来：数据面每次写入都会调这个方法，热加载的检查必须和预占
    /// 在同一把锁里一次做完，而不是先 Configure 再 Reserve 拿两次锁。
    /// </summary>
    public TimeSpan Reserve(double newRate, double newCapacity, int bytes)
    {
        lock (gate)
        {
            Reconfigure(newRate, newCapacity);

            tokens -= bytes;
            if (tokens >= 0 || rate <= 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(-tokens / rate);
        }
    }

    /// <summary>预占 bytes 个字节（速率沿用当前配置）。</summary>
    public TimeSpan Reserve(int bytes) => Reserve(rate, capacity, bytes);

    private void Reconfigure(double newRate, double newCapacity)
    {
        if (Math.Abs(rate - newRate) < 0.0001 && Math.Abs(capacity - newCapacity) < 0.0001)
        {
            return;
        }

        Refill();
        rate = newRate;
        capacity = newCapacity;
        tokens = Math.Min(tokens, capacity);
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - lastTicks) / (double)Stopwatch.Frequency;
        lastTicks = now;

        if (elapsed <= 0)
        {
            return;
        }

        tokens = Math.Min(capacity, tokens + elapsed * rate);
    }
}
