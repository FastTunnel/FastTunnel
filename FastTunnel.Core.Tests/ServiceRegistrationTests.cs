// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Metrics;
using FastTunnel.Core.Security;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// S2 加进来的几个单例与可选构造参数必须能在真实 DI 容器里解析出来，
/// 否则编译通过但服务端启动即失败。这里用内存配置禁用文件持久化，避免污染工作目录。
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void 封禁组件可解析且服务端拿到同一个BanRegistry实例()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FastTunnel:WebDomain"] = "test.cc",
                ["FastTunnel:Abuse:Ban:StateFile"] = "",
                ["FastTunnel:Abuse:Audit:FilePath"] = "",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFastTunnelServer(configuration.GetSection("FastTunnel"));

        using var provider = services.BuildServiceProvider();

        var bans = provider.GetRequiredService<BanRegistry>();
        var audit = provider.GetRequiredService<AuditLog>();
        var service = provider.GetRequiredService<BanService>();
        var server = provider.GetRequiredService<FastTunnelServer>();
        var loginHandler = provider.GetRequiredService<ILoginHandler>();
        var clientHandler = provider.GetRequiredService<FastTunnelClientHandler>();
        var meter = provider.GetRequiredService<TrafficMeter>();
        var directory = provider.GetRequiredService<ClientTokenDirectory>();

        Assert.Same(bans, provider.GetRequiredService<BanRegistry>());
        Assert.Same(audit, provider.GetRequiredService<AuditLog>());
        Assert.Same(bans, server.Bans);
        Assert.Same(meter, server.Traffic);
        Assert.Same(meter, provider.GetRequiredService<TrafficMeter>());
        Assert.Same(directory, provider.GetRequiredService<ClientTokenDirectory>());
        Assert.NotNull(service);
        Assert.NotNull(loginHandler);
        Assert.NotNull(clientHandler);
    }
}
