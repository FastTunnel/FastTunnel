// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using FastTunnel.Core.Config;
using FastTunnel.Core.Tests.Support;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 自签证书的生成与复用。SAN 是重点：客户端用系统 CA 路径校验时，
/// 少了 127.0.0.1 / ::1 就会报 RemoteCertificateNameMismatch——这正是本轮 IPv6 改造踩过的坑。
/// </summary>
public class ServerCertificateProviderTests
{
    private static ServerCertificateProvider CreateProvider(
        string contentRoot,
        string? certFile = null,
        bool autoGenerate = true,
        bool enabled = true,
        string? webDomain = "test.test.cc")
        => new(
            new TestOptionsMonitor<DefaultServerConfig>(new DefaultServerConfig
            {
                WebDomain = webDomain!,
                Tls = new DefaultServerConfig.TlsOptions
                {
                    Enabled = enabled,
                    CertFile = certFile!,
                    AutoGenerate = autoGenerate,
                },
            }),
            new TestHostEnvironment(contentRoot),
            NullLogger<ServerCertificateProvider>.Instance);

    [Fact]
    public void 首次调用生成自签证书并落盘()
    {
        using var dir = new TempDirectory();
        var provider = CreateProvider(dir.Path);

        var certificate = provider.GetCertificate();

        Assert.NotNull(certificate);
        Assert.True(File.Exists(Path.Combine(dir.Path, "config", "ft-server.pfx")));
    }

    [Fact]
    public void 再次调用复用同一份证书()
    {
        using var dir = new TempDirectory();

        var first = CreateProvider(dir.Path).GetCertificate();
        var second = CreateProvider(dir.Path).GetCertificate();

        // 换一个 Provider 实例（等价于服务端重启）也要读到同一个文件
        Assert.Equal(TunnelCertificate.GetFingerprint(first), TunnelCertificate.GetFingerprint(second));
    }

    [Fact]
    public void SAN包含回环地址与主机名()
    {
        using var dir = new TempDirectory();
        var certificate = CreateProvider(dir.Path, webDomain: null).GetCertificate();

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        var dnsNames = san.EnumerateDnsNames().ToArray();
        var ipAddresses = san.EnumerateIPAddresses().ToArray();

        Assert.Contains("localhost", dnsNames);
        Assert.Contains(Environment.MachineName, dnsNames);

        // IPv4 与 IPv6 回环都必须有，否则客户端配 127.0.0.1 / ::1 直连时名称校验失败
        Assert.Contains(IPAddress.Loopback, ipAddresses);
        Assert.Contains(IPAddress.IPv6Loopback, ipAddresses);
    }

    [Fact]
    public void SAN包含WebDomain及其通配()
    {
        using var dir = new TempDirectory();
        var certificate = CreateProvider(dir.Path, webDomain: "test.test.cc").GetCertificate();

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var dnsNames = san.EnumerateDnsNames().ToArray();

        Assert.Contains("test.test.cc", dnsNames);
        Assert.Contains("*.test.test.cc", dnsNames);
    }

    [Fact]
    public void 链路本地IPv6地址不进SAN()
    {
        using var dir = new TempDirectory();
        var certificate = CreateProvider(dir.Path).GetCertificate();

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        // 带 scope id 的链路本地地址写进 SAN 没有意义
        Assert.DoesNotContain(san.EnumerateIPAddresses(), a => a.IsIPv6LinkLocal || a.IsIPv6Multicast);
    }

    [Fact]
    public void 证书有效期覆盖一段合理区间()
    {
        using var dir = new TempDirectory();
        var certificate = CreateProvider(dir.Path).GetCertificate();

        Assert.True(certificate.NotBefore <= DateTime.Now);
        Assert.True(certificate.NotAfter > DateTime.Now.AddYears(1));
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void 配置了证书文件时优先使用()
    {
        using var dir = new TempDirectory();

        // 先用默认逻辑生成一份，再让配置显式指向它
        var generated = CreateProvider(dir.Path).GetCertificate();
        var certPath = Path.Combine(dir.Path, "config", "ft-server.pfx");
        var expected = TunnelCertificate.GetFingerprint(generated);

        var provider = CreateProvider(dir.Path, certFile: certPath);

        Assert.Equal(expected, TunnelCertificate.GetFingerprint(provider.GetCertificate()));
    }

    [Fact]
    public void 证书文件不存在时报错()
    {
        using var dir = new TempDirectory();
        var provider = CreateProvider(dir.Path, certFile: Path.Combine(dir.Path, "missing.pfx"));

        // 配错路径必须在启动时暴露，而不是等到第一个客户端连进来
        Assert.Throws<FileNotFoundException>(() => provider.GetCertificate());
    }

    [Fact]
    public void 不自动生成且未配证书时报错()
    {
        using var dir = new TempDirectory();
        var provider = CreateProvider(dir.Path, autoGenerate: false);

        Assert.Throws<InvalidOperationException>(() => provider.GetCertificate());
    }

    [Fact]
    public void TLS关闭时不生成证书()
    {
        using var dir = new TempDirectory();
        var provider = CreateProvider(dir.Path, enabled: false);

        provider.EnsureReady();

        Assert.False(File.Exists(Path.Combine(dir.Path, "config", "ft-server.pfx")));
    }

    [Fact]
    public void TLS启用时EnsureReady会预热证书()
    {
        using var dir = new TempDirectory();
        var provider = CreateProvider(dir.Path);

        provider.EnsureReady();

        Assert.True(File.Exists(Path.Combine(dir.Path, "config", "ft-server.pfx")));
    }

    [Fact]
    public void 生成的证书指纹可直接用于客户端固定()
    {
        using var dir = new TempDirectory();
        var certificate = CreateProvider(dir.Path).GetCertificate();

        var fingerprint = TunnelCertificate.GetFingerprint(certificate);

        var accepted = TunnelCertificate.Validate(
            certificate,
            System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            new SuiDaoServer
            {
                ServerAddr = "127.0.0.1",
                ServerPort = 1270,
                CertificateFingerprint = fingerprint,
            },
            NullLogger.Instance);

        Assert.True(accepted);
    }
}
