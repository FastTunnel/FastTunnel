// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Security.Claims;
using FastTunnel.Api.Controllers;
using FastTunnel.Core.Config;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using FastTunnel.Core.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>运维端点：匿名探活 / Prometheus 文本 / 关闭匿名时要求认证。</summary>
public class OpsControllerTests
{
    private sealed class Harness
    {
        public Harness(bool publicEndpoints = true, bool includeClientLabels = true)
        {
            Config = new DefaultServerConfig
            {
                WebDomain = "test.cc",
                Clients = new List<ClientCredentialOptions>
                {
                    new() { Id = "alice", Token = "tok-alice" },
                },
                Ops = new OpsOptions
                {
                    PublicEndpoints = publicEndpoints,
                    IncludeClientLabels = includeClientLabels,
                },
            };

            Monitor = new TestOptionsMonitor<DefaultServerConfig>(Config);
            Server = new FastTunnelServer(NullLogger<FastTunnelServer>.Instance, Monitor);
            Meter = new TrafficMeter(NullLogger<TrafficMeter>.Instance, Monitor);
            Directory = new ClientTokenDirectory(NullLogger<ClientTokenDirectory>.Instance, Monitor);
            Controller = new OpsController(Server, Meter, Directory, Monitor)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() },
                },
            };
        }

        public DefaultServerConfig Config { get; }

        public IOptionsMonitor<DefaultServerConfig> Monitor { get; }

        public FastTunnelServer Server { get; }

        public TrafficMeter Meter { get; }

        public ClientTokenDirectory Directory { get; }

        public OpsController Controller { get; }
    }

    [Fact]
    public void healthz匿名返回200与汇总状态()
    {
        var h = new Harness();
        h.Meter.RecordRequest();

        var result = Assert.IsType<OkObjectResult>(h.Controller.Health());
        Assert.Equal(200, result.StatusCode);
        var text = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"status\":\"ok\"", text);
        Assert.Contains("\"requestsTotal\":1", text);
        Assert.Contains("\"named\":1", text);
        Assert.DoesNotContain("tok-alice", text); // 凭据明文不得出现在健康响应里
    }

    [Fact]
    public void metrics返回Prometheus文本且内容类型正确()
    {
        var h = new Harness();
        h.Meter.RecordRequest();

        var result = Assert.IsType<ContentResult>(h.Controller.Metrics());
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", result.ContentType);
        Assert.Contains("fasttunnel_up 1.000", result.Content);
        Assert.Contains("fasttunnel_requests_total 1", result.Content);
        Assert.Contains("# TYPE fasttunnel_proxy_bytes_total counter", result.Content);
    }

    [Fact]
    public void 关闭匿名后未认证请求被拒绝()
    {
        var h = new Harness(publicEndpoints: false);

        Assert.IsType<UnauthorizedObjectResult>(h.Controller.Health());
        Assert.IsType<UnauthorizedObjectResult>(h.Controller.Metrics());
    }

    [Fact]
    public void 认证用户即使关闭匿名也能访问()
    {
        var h = new Harness(publicEndpoints: false);
        h.Controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") }, "test"));

        Assert.IsType<OkObjectResult>(h.Controller.Health());
        Assert.IsType<ContentResult>(h.Controller.Metrics());
    }

    [Fact]
    public async Task 关闭client标签后指标里不出现主体Id()
    {
        var h = new Harness(includeClientLabels: false);
        var pipe = new System.IO.Pipelines.Pipe();
        await h.Meter.Wrap(pipe.Writer, new TrafficScope("a.test.cc", "alice"), TrafficDirection.In)
            .WriteAsync(new byte[] { 1, 2, 3 });

        var result = Assert.IsType<ContentResult>(h.Controller.Metrics());
        Assert.Contains("proxy=\"a.test.cc\"", result.Content);
        Assert.DoesNotContain("client=\"alice\"", result.Content);
    }
}
