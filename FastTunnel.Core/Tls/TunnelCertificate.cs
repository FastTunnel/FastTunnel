// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Tls;

/// <summary>
/// 客户端校验服务端证书的单一入口：控制通道（WebSocket）与数据通道（SslStream）共用同一套策略。
///
/// 判定顺序：
/// 1. 配置了指纹 → 只认指纹（最适合自签证书，也防得住中间人）；
/// 2. 系统 CA 校验通过 → 放行；
/// 3. 显式开启 AllowUntrustedCertificate → 放行并告警；
/// 4. 其余一律拒绝，并在日志里给出可用于固定的指纹。
/// </summary>
public static class TunnelCertificate
{
    public static RemoteCertificateValidationCallback CreateCallback(SuiDaoServer server, ILogger logger)
        => (_, certificate, _, errors) => Validate(certificate, errors, server, logger);

    public static bool Validate(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        SuiDaoServer server,
        ILogger logger)
    {
        if (certificate == null)
        {
            logger.LogError("服务端未提供证书，连接已拒绝");
            return false;
        }

        var fingerprint = GetFingerprint(certificate);
        var pinned = Normalize(server.CertificateFingerprint);

        if (pinned.Length > 0)
        {
            if (string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            logger.LogError(
                "服务端证书指纹不匹配，连接已拒绝。期望 {Expected}，实际 {Actual}（实际指纹 {Subject}）",
                pinned, fingerprint, certificate.Subject);
            return false;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (server.AllowUntrustedCertificate)
        {
            logger.LogWarning(
                "已跳过服务端证书校验（AllowUntrustedCertificate=true）：连接已加密，但无法证明对端身份。服务端证书指纹 {Fingerprint}，校验问题 {Errors}",
                fingerprint, errors);
            return true;
        }

        logger.LogError(
            "服务端证书校验失败（{Errors}）。自签证书请把服务端启动日志里的 SHA-256 指纹配置到 FastTunnel:Server:CertificateFingerprint；仅开发环境可临时设置 AllowUntrustedCertificate=true。服务端实际指纹 {Fingerprint}",
            errors, fingerprint);
        return false;
    }

    /// <summary>SHA-256 指纹，大写十六进制、无分隔符。</summary>
    public static string GetFingerprint(X509Certificate certificate)
        => Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>
    /// 指纹归一化：容忍用户从日志/openssl 里复制时带上的冒号、空格、连字符和小写。
    /// </summary>
    public static string Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        var chars = new char[fingerprint.Length];
        var index = 0;

        foreach (var c in fingerprint)
        {
            if (c is ':' or '-' or ' ' or '\t' || char.IsWhiteSpace(c))
            {
                continue;
            }

            chars[index++] = char.ToUpperInvariant(c);
        }

        return new string(chars, 0, index);
    }
}
