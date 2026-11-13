// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Tls;

/// <summary>
/// 服务端 TLS 证书来源：优先用配置里的证书文件，其次复用上次自动生成的自签证书，
/// 都没有则生成一份并落盘。这样自建用户不需要先去申请证书才能用上加密。
/// </summary>
public sealed class ServerCertificateProvider
{
    /// <summary>自动生成证书的默认路径（相对内容根目录）。</summary>
    private const string AutoCertRelativePath = "config/ft-server.pfx";

    private readonly IOptionsMonitor<DefaultServerConfig> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ServerCertificateProvider> _logger;

    private readonly object _lock = new();
    private X509Certificate2? _certificate;

    public ServerCertificateProvider(
        IOptionsMonitor<DefaultServerConfig> options,
        IHostEnvironment environment,
        ILogger<ServerCertificateProvider> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public DefaultServerConfig.TlsOptions Options => _options.CurrentValue.Tls ?? new DefaultServerConfig.TlsOptions();

    /// <summary>
    /// 取证书，必要时生成。首次调用会记录日志（含指纹），方便用户固定到客户端。
    /// </summary>
    public X509Certificate2 GetCertificate()
    {
        if (_certificate != null)
        {
            return _certificate;
        }

        lock (_lock)
        {
            return _certificate ??= Load();
        }
    }

    /// <summary>
    /// 启动时预热：让证书错误尽早暴露，并把指纹打到日志里。
    /// </summary>
    public void EnsureReady()
    {
        if (!Options.Enabled)
        {
            _logger.LogInformation("客户端通道 TLS 未启用，客户端请使用 Protocol=ws 接入（明文）");
            return;
        }

        GetCertificate();
    }

    private X509Certificate2 Load()
    {
        var options = Options;

        if (!string.IsNullOrWhiteSpace(options.CertFile))
        {
            var path = ResolvePath(options.CertFile!);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"TLS 证书文件不存在: {path}", path);
            }

            var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, options.CertPassword);
            LogCertificate(loaded, path);
            return loaded;
        }

        if (!options.AutoGenerate)
        {
            throw new InvalidOperationException(
                "TLS 已启用但未配置证书：请设置 FastTunnel:Tls:CertFile，或开启 FastTunnel:Tls:AutoGenerate 自动生成自签证书");
        }

        var autoPath = ResolvePath(AutoCertRelativePath);
        if (File.Exists(autoPath))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(autoPath, password: null);
            LogCertificate(existing, autoPath);
            return existing;
        }

        var generated = Generate(options.CertPassword);
        Directory.CreateDirectory(Path.GetDirectoryName(autoPath)!);
        File.WriteAllBytes(autoPath, generated.Export(X509ContentType.Pfx, options.CertPassword));
        LogCertificate(generated, autoPath);
        return generated;
    }

    private void LogCertificate(X509Certificate2 certificate, string path)
    {
        _logger.LogInformation(
            "客户端通道 TLS 已启用，证书 {Subject} 有效期至 {Expire:yyyy-MM-dd}，SHA-256 指纹 {Fingerprint}（客户端可用 FastTunnel:Server:CertificateFingerprint 固定）",
            certificate.Subject,
            certificate.NotAfter,
            TunnelCertificate.GetFingerprint(certificate));
        _logger.LogDebug("证书来源：{Path}", path);
    }

    private string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path);

    /// <summary>
    /// 生成自签证书。SAN 里带上 WebDomain（含通配）、主机名与本机 IP，
    /// 这样按域名访问时客户端做的是正常的主机名校验。
    /// </summary>
    private X509Certificate2 Generate(string? password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FastTunnel", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);

        var machineName = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            san.AddDnsName(machineName);
        }

        var webDomain = _options.CurrentValue.WebDomain;
        if (!string.IsNullOrWhiteSpace(webDomain) && webDomain != "*")
        {
            san.AddDnsName(webDomain);
            san.AddDnsName($"*.{webDomain}");
        }

        foreach (var address in GetLocalAddresses())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        // CreateSelfSigned 返回的对象绑定了当前进程的私钥，重新导入一份便于导出和后续使用
        var pfx = certificate.Export(X509ContentType.Pfx, password);
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
    }

    private static System.Collections.Generic.IEnumerable<IPAddress> GetLocalAddresses()
    {
        var result = new System.Collections.Generic.List<IPAddress>
        {
            // 回环地址显式加进去：客户端配 127.0.0.1 / ::1 直连时，系统 CA 校验路径也能过
            IPAddress.Loopback,
            IPAddress.IPv6Loopback,
        };

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var info in ni.GetIPProperties().UnicastAddresses)
                {
                    var address = info.Address;

                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        result.Add(address);
                        continue;
                    }

                    // IPv6：链路本地/站点本地带 scope id，写进 SAN 没有意义，也会让校验方困惑
                    if (address.AddressFamily == AddressFamily.InterNetworkV6
                        && !address.IsIPv6LinkLocal
                        && !address.IsIPv6SiteLocal
                        && !address.IsIPv6Multicast)
                    {
                        result.Add(address);
                    }
                }
            }
        }
        catch
        {
            // 取不到网卡信息不影响证书可用性，客户端仍可用指纹固定
        }

        return result.Distinct().ToArray();
    }
}
