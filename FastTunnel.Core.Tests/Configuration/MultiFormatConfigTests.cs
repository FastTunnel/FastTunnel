// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.IO;
using FastTunnel.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FastTunnel.Core.Tests.Configuration;

/// <summary>
/// 多格式配置文件加载测试：INI 的解析与扁平化、格式间优先级（后加载覆盖先加载）、
/// 环境变体覆盖、必有一个基础配置的启动语义。
/// </summary>
public class MultiFormatConfigTests
{
    private class SampleClient
    {
        public string? Id { get; set; }
        public string? Token { get; set; }
        public int BandwidthLimit { get; set; }
    }

    private class SampleFastTunnel
    {
        public int BinPort { get; set; }
        public List<SampleClient> Clients { get; set; } = new();
        public bool EnforceSingleSession { get; set; }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ftcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IConfigurationRoot Build(string dir, string? environmentName = null)
    {
        var builder = new ConfigurationBuilder();
        builder.SetBasePath(dir);
        builder.AddFastTunnelConfigFiles(".", environmentName);
        return builder.Build();
    }

    [Fact]
    public void Ini_FlattensSectionsAndIndexedArrays()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.ini"), """
                [FastTunnel]
                BinPort=1270
                EnforceSingleSession=True

                [FastTunnel:Clients:0]
                Id=alice
                Token=token-alice
                BandwidthLimit=1048576

                [FastTunnel:Clients:1]
                Id=bob
                Token=token-bob
                BandwidthLimit=2048
                """);

            var config = Build(dir);

            var bound = config.GetSection("FastTunnel").Get<SampleFastTunnel>();
            Assert.NotNull(bound);
            Assert.Equal(1270, bound!.BinPort);
            Assert.True(bound.EnforceSingleSession);
            Assert.Equal(2, bound.Clients.Count);
            Assert.Equal("alice", bound.Clients[0].Id);
            Assert.Equal("token-alice", bound.Clients[0].Token);
            Assert.Equal(1048576, bound.Clients[0].BandwidthLimit);
            Assert.Equal("bob", bound.Clients[1].Id);
            Assert.Equal(2048, bound.Clients[1].BandwidthLimit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ini_OverridesJson()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{ "FastTunnel": { "BinPort": 1270, "Origin": "json" } }""");
            File.WriteAllText(Path.Combine(dir, "appsettings.ini"),
                """
                [FastTunnel]
                Origin=ini
                """);

            var config = Build(dir);

            // json 里的独有键保留
            Assert.Equal(1270, config.GetValue<int>("FastTunnel:BinPort"));
            // 两个格式都定义了 Origin：ini（后加载）胜出
            Assert.Equal("ini", config["FastTunnel:Origin"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentVariant_OverridesBaseFile()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{ "FastTunnel": { "BinPort": 1270, "EnforceSingleSession": false } }""");
            File.WriteAllText(Path.Combine(dir, "appsettings.Production.ini"),
                """
                [FastTunnel]
                EnforceSingleSession=True
                """);

            var config = Build(dir, environmentName: "Production");

            Assert.Equal(1270, config.GetValue<int>("FastTunnel:BinPort"));
            Assert.True(config.GetValue<bool>("FastTunnel:EnforceSingleSession"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoBaseFile_Throws_FileNotFound()
    {
        var dir = CreateTempDir();
        try
        {
            var ex = Assert.Throws<FileNotFoundException>(() => Build(dir, "Production"));
            Assert.Contains("appsettings", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IncludeJsonFalse_SkipsJsonAndDoesNotRequireBase()
    {
        var dir = CreateTempDir();
        try
        {
            // 只有 json：includeJson=false 时不应加载、也不应因缺基础配置而抛异常
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{ "FastTunnel": { "Origin": "json" } }""");

            var builder = new ConfigurationBuilder();
            builder.SetBasePath(dir);
            builder.AddFastTunnelConfigFiles(".", includeJson: false);
            var config = builder.Build();

            Assert.Null(config["FastTunnel:Origin"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
