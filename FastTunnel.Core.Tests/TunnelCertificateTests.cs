// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FastTunnel.Core.Config;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>
/// 客户端校验服务端证书的策略。四个分支的优先级必须固定：
/// 指纹固定 &gt; 系统 CA &gt; 显式允许不受信 &gt; 拒绝。
/// 顺序错了就会出现「配了指纹却仍被坏证书放行」这类安全问题。
/// </summary>
public class TunnelCertificateTests
{
    private static SuiDaoServer CreateServer(string? fingerprint = null, bool allowUntrusted = false)
        => new()
        {
            ServerAddr = "127.0.0.1",
            ServerPort = 1270,
            CertificateFingerprint = fingerprint!,
            AllowUntrustedCertificate = allowUntrusted,
        };

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FastTunnel-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddDays(-1), System.DateTimeOffset.UtcNow.AddDays(1));
    }

    // ---------------- Normalize ----------------

    [Theory]
    [InlineData("AB:CD:EF", "ABCDEF")]
    [InlineData("ab:cd:ef", "ABCDEF")]
    [InlineData("AB-CD-EF", "ABCDEF")]
    [InlineData("AB CD EF", "ABCDEF")]
    [InlineData(" AB\tCD\r\nEF ", "ABCDEF")]
    [InlineData("{}", "{}")]
    public void Normalize_容忍分隔符与大小写(string input, string expected)
        => Assert.Equal(expected, TunnelCertificate.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::::")]
    public void Normalize_空指纹归一为空串(string? input)
        => Assert.Equal(string.Empty, TunnelCertificate.Normalize(input));

    // ---------------- GetFingerprint ----------------

    [Fact]
    public void GetFingerprint_是64位大写十六进制()
    {
        using var cert = CreateSelfSigned();

        var fingerprint = TunnelCertificate.GetFingerprint(cert);

        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint.ToUpperInvariant(), fingerprint);
        Assert.All(fingerprint, c => Assert.True(char.IsAsciiHexDigit(c)));
        Assert.Equal(cert.GetCertHashString(HashAlgorithmName.SHA256), fingerprint);
    }

    // ---------------- Validate ----------------

    [Fact]
    public void Validate_指纹匹配即放行_即使系统校验失败()
    {
        using var cert = CreateSelfSigned();
        var fingerprint = TunnelCertificate.GetFingerprint(cert);

        // 故意把系统校验结果设成错误，验证指纹分支的优先级更高
        var accepted = TunnelCertificate.Validate(
            cert, SslPolicyErrors.RemoteCertificateChainErrors,
            CreateServer(fingerprint), NullLogger.Instance);

        Assert.True(accepted);
    }

    [Fact]
    public void Validate_指纹可省略分隔符与小写()
    {
        using var cert = CreateSelfSigned();
        var fingerprint = TunnelCertificate.GetFingerprint(cert);

        var messy = string.Join(":", SplitInto(fingerprint, 2)).ToLowerInvariant();

        var accepted = TunnelCertificate.Validate(
            cert, SslPolicyErrors.None, CreateServer(messy), NullLogger.Instance);

        Assert.True(accepted);
    }

    [Fact]
    public void Validate_指纹不匹配必须拒绝_哪怕系统CA验过也没用()
    {
        using var cert = CreateSelfSigned();
        using var other = CreateSelfSigned();

        var accepted = TunnelCertificate.Validate(
            cert, SslPolicyErrors.None,
            CreateServer(TunnelCertificate.GetFingerprint(other)), NullLogger.Instance);

        Assert.False(accepted);
    }

    [Fact]
    public void Validate_未配指纹且系统校验通过则放行()
    {
        using var cert = CreateSelfSigned();

        Assert.True(TunnelCertificate.Validate(cert, SslPolicyErrors.None, CreateServer(), NullLogger.Instance));
    }

    [Fact]
    public void Validate_未配指纹且校验失败默认拒绝()
    {
        using var cert = CreateSelfSigned();

        Assert.False(TunnelCertificate.Validate(
            cert, SslPolicyErrors.RemoteCertificateNameMismatch, CreateServer(), NullLogger.Instance));
    }

    [Fact]
    public void Validate_显式允许不受信时可放行()
    {
        using var cert = CreateSelfSigned();

        Assert.True(TunnelCertificate.Validate(
            cert, SslPolicyErrors.RemoteCertificateChainErrors,
            CreateServer(allowUntrusted: true), NullLogger.Instance));
    }

    [Fact]
    public void Validate_对端未提供证书时拒绝()
    {
        Assert.False(TunnelCertificate.Validate(
            null, SslPolicyErrors.RemoteCertificateNotAvailable, CreateServer(), NullLogger.Instance));
    }

    [Fact]
    public void CreateCallback_走的是同一套策略()
    {
        using var cert = CreateSelfSigned();
        var fingerprint = TunnelCertificate.GetFingerprint(cert);

        var accepting = TunnelCertificate.CreateCallback(CreateServer(fingerprint), NullLogger.Instance);
        Assert.True(accepting(this, cert, null, SslPolicyErrors.RemoteCertificateChainErrors));

        var rejecting = TunnelCertificate.CreateCallback(CreateServer("00"), NullLogger.Instance);
        Assert.False(rejecting(this, cert, null, SslPolicyErrors.None));
    }

    private static string[] SplitInto(string value, int size)
    {
        var parts = new string[(value.Length + size - 1) / size];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = value.Substring(i * size, System.Math.Min(size, value.Length - i * size));
        }

        return parts;
    }
}
