// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 按代理计量 + 令牌桶限速。重点验证两条写入方式都不重复计数、不限速时零额外等待，
/// 以及 Prometheus 输出的标签转义。
/// </summary>
public class TrafficMeterTests
{
    private static TrafficMeter Meter(DefaultServerConfig? config = null)
        => new(NullLogger<TrafficMeter>.Instance,
            new TestOptionsMonitor<DefaultServerConfig>(config ?? new DefaultServerConfig()));

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("hello fasttunnel");

    [Fact]
    public async Task CopyToAsync写入被精确计量且不丢字节()
    {
        var meter = Meter();
        var source = new Pipe();
        var target = new Pipe();
        await source.Writer.WriteAsync(Payload);
        await source.Writer.CompleteAsync();

        var writer = meter.Wrap(target.Writer, new TrafficScope("a.test.cc", "alice"), TrafficDirection.In);
        await source.Reader.CopyToAsync(writer);
        await writer.CompleteAsync();

        var received = await TestPipes.ReadToEndAsync(target.Reader);
        Assert.Equal(Payload, received);

        var snapshot = meter.Snapshot();
        Assert.Equal((long)Payload.Length, snapshot.TotalBytesIn);
        Assert.Equal(0, snapshot.TotalBytesOut);
        Assert.Equal((long)Payload.Length, Assert.Single(snapshot.Proxies).BytesIn);
        Assert.Equal("alice", snapshot.Proxies[0].Client);
    }

    [Fact]
    public async Task GetMemory_Advance_Flush路径只计一次不重复()
    {
        var meter = Meter();
        var target = new Pipe();
        var writer = meter.Wrap(target.Writer, new TrafficScope("b.test.cc", null), TrafficDirection.Out);

        var memory = writer.GetMemory(Payload.Length);
        Payload.CopyTo(memory);
        writer.Advance(Payload.Length);
        await writer.FlushAsync();

        // 再 Flush 一次（pending 已清零），不应重复计数
        await writer.FlushAsync();
        await writer.CompleteAsync();

        Assert.Equal(Payload, await TestPipes.ReadToEndAsync(target.Reader));
        var snapshot = meter.Snapshot();
        Assert.Equal((long)Payload.Length, snapshot.TotalBytesOut);
        Assert.Equal("anonymous", Assert.Single(snapshot.Proxies).Client);
    }

    [Fact]
    public async Task Stream装饰器双向计量()
    {
        var meter = Meter();
        var backing = new MemoryStream();
        var writeOnly = new TrafficScope("port:12701", "alice");

        var wrapped = meter.WrapBidirectional(backing, writeOnly,
            TrafficDirection.In, TrafficDirection.Out);
        await wrapped.WriteAsync(Payload);

        backing.Position = 0;
        var buffer = new byte[Payload.Length];
        var read = await wrapped.ReadAsync(buffer.AsMemory());

        Assert.Equal(Payload.Length, read);
        Assert.Equal(Payload, buffer);
        var snapshot = meter.Snapshot();
        Assert.Equal((long)Payload.Length, snapshot.TotalBytesIn);
        Assert.Equal((long)Payload.Length, snapshot.TotalBytesOut);
    }

    [Fact]
    public async Task 速率优先级为按代理_按主体_凭据_全局()
    {
        var config = new DefaultServerConfig();
        config.Bandwidth.DefaultBytesPerSecond = 1_000_000;
        config.Bandwidth.PerClient["bob"] = 2_000_000;
        config.Bandwidth.PerProxy["c.test.cc"] = 3_000_000;
        var meter = Meter(config);

        var target = new Pipe();
        await meter.Wrap(target.Writer, new TrafficScope("a.test.cc", "alice"), TrafficDirection.In)
            .WriteAsync(Payload);
        await meter.Wrap(target.Writer, new TrafficScope("b.test.cc", "bob"), TrafficDirection.In)
            .WriteAsync(Payload);
        await meter.Wrap(target.Writer, new TrafficScope("c.test.cc", "bob"), TrafficDirection.In)
            .WriteAsync(Payload);
        await meter.Wrap(target.Writer, new TrafficScope("d.test.cc", "carol", 4_000_000), TrafficDirection.In)
            .WriteAsync(Payload);

        var limits = meter.Snapshot().Proxies.ToDictionary(x => x.Proxy);
        Assert.Equal(1_000_000L, ByProxy(meter, "a.test.cc"));
        Assert.Equal(2_000_000L, ByProxy(meter, "b.test.cc"));
        Assert.Equal(3_000_000L, ByProxy(meter, "c.test.cc"));
        Assert.Equal(4_000_000L, ByProxy(meter, "d.test.cc"));
        Assert.Equal(4, limits.Count);
    }

    [Fact]
    public async Task 显式零速率表示不限速()
    {
        var config = new DefaultServerConfig();
        config.Bandwidth.DefaultBytesPerSecond = 5_000_000;
        var meter = Meter(config);
        var target = new Pipe();

        await meter.Wrap(target.Writer, new TrafficScope("free.test.cc", "alice", 0), TrafficDirection.In)
            .WriteAsync(Payload);

        Assert.Equal(0L, ByProxy(meter, "free.test.cc"));
        Assert.Equal(0L, meter.Snapshot().Proxies[0].LimitBytesPerSecond);
        Assert.Equal(0d, meter.Snapshot().ThrottleDelaySeconds);
    }

    [Fact]
    public void 令牌桶在超额时返回等待时长()
    {
        var bucket = new TokenBucket(1000, 1000);

        Assert.Equal(TimeSpan.Zero, bucket.Reserve(400));
        var delay = bucket.Reserve(1000); // 余 600，超 400 → 0.4s

        Assert.InRange(delay.TotalSeconds, 0.35, 0.45);
        Assert.Equal(TimeSpan.Zero, new TokenBucket(1000, 1000).Reserve(0));
    }

    [Fact]
    public void 连接计数增减平衡()
    {
        var meter = Meter();
        var scope = new TrafficScope("a.test.cc", "alice");

        meter.ConnectionOpened(scope);
        meter.ConnectionOpened(scope);
        Assert.Equal(2L, meter.Snapshot().ActiveConnections);
        Assert.Equal(2L, meter.Snapshot().Proxies[0].ConnectionsTotal);

        meter.ConnectionClosed(scope);
        Assert.Equal(1L, meter.Snapshot().ActiveConnections);
        Assert.Equal(1L, meter.Snapshot().Proxies[0].ActiveConnections);
    }

    [Fact]
    public async Task Prometheus输出转义标签且可关闭主体标签()
    {
        var meter = Meter();
        var target = new Pipe();
        await meter.Wrap(target.Writer, new TrafficScope("bad\"proxy", "a\\b"), TrafficDirection.In)
            .WriteAsync(Payload);
        meter.RecordRequest();
        meter.RecordRejection(true);
        meter.RecordRejection(false);

        var server = new ServerStats { Version = "3.3.0", ClientsOnline = 2, WebMappings = 3 };
        var text = PrometheusText.Render(meter.Snapshot(), server, includeClientLabels: true);

        Assert.Contains("fasttunnel_up 1.000", text);
        Assert.Contains("# TYPE fasttunnel_proxy_bytes_total counter", text);
        Assert.Contains("proxy=\"bad\\\"proxy\",client=\"a\\\\b\"", text);
        Assert.Contains("fasttunnel_rejected_total{reason=\"banned\"} 1", text);
        Assert.Contains("fasttunnel_rejected_total{reason=\"allowlist\"} 1", text);
        Assert.Contains("fasttunnel_build_info{version=\"3.3.0\"} 1", text);

        var anonymized = PrometheusText.Render(meter.Snapshot(), server, includeClientLabels: false);
        Assert.DoesNotContain("client=", anonymized);
        Assert.Contains("proxy=\"bad\\\"proxy\"}", anonymized);
    }

    private static long ByProxy(TrafficMeter meter, string proxy)
    {
        foreach (var row in meter.Snapshot().Proxies)
        {
            if (string.Equals(row.Proxy, proxy, StringComparison.OrdinalIgnoreCase))
            {
                return row.LimitBytesPerSecond;
            }
        }

        throw new Xunit.Sdk.XunitException($"未找到代理 {proxy} 的计量记录");
    }
}
