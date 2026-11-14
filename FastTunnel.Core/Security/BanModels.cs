// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

// 内部数据模型，不生成 XML 文档注释
#pragma warning disable CS1591

using System;

namespace FastTunnel.Core.Security;

/// <summary>封禁主体类型。Account 目前没有独立账号体系，仅保留建模位。</summary>
public enum BanSubjectType
{
    Token = 0,
    Host = 1,
    Account = 2,
}

/// <summary>
/// 处置级别。只有 <see cref="Block"/> 及以上会真正拦截请求；
/// Observe / Throttle 只用于记录与告警，限速动作尚未实现。
/// </summary>
public enum BanLevel
{
    Observe = 0,
    Throttle = 1,
    Block = 2,
    Permanent = 3,
}

public enum ReviewState
{
    Pending = 0,
    Confirmed = 1,
    Rejected = 2,
}

/// <summary>一条封禁 / 观察 / 举报记录。Subject 在入库前会被规范化：token 只存指纹。</summary>
public sealed class BanRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>一次熔断可能同时产生主体记录与若干关联域名记录，用 GroupId 一起解封 / 复核。</summary>
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");

    public BanSubjectType SubjectType { get; set; }

    public string Subject { get; set; } = string.Empty;

    public BanLevel Level { get; set; } = BanLevel.Block;

    public ReviewState ReviewState { get; set; } = ReviewState.Pending;

    public string? RuleId { get; set; }

    public double Score { get; set; }

    public string? EvidenceId { get; set; }

    public string? Reason { get; set; }

    public string? Operator { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>为空表示不自动解封。违法内容不应因超时自动恢复，解封必须走人工复核。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public string? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewNote { get; set; }

    public bool IsActive(DateTimeOffset now)
        => Level >= BanLevel.Block
           && ReviewState != ReviewState.Rejected
           && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>封禁命令。API 层的请求模型在 FastTunnel.Api 里，转换成它再交给 BanService。</summary>
public sealed class BanCommand
{
    public BanSubjectType SubjectType { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? RuleId { get; set; }

    public double Score { get; set; }

    public string? EvidenceId { get; set; }

    public string? Operator { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>请求级别。Permanent 会被强制降级为 Block——永封只能由人工复核升级。</summary>
    public BanLevel RequestedLevel { get; set; } = BanLevel.Block;

    // 以下为证据字段，随审计日志一起留存
    public string? ClientIp { get; set; }

    public string? Host { get; set; }

    public string? Path { get; set; }

    public string? Method { get; set; }

    public string? TokenFingerprint { get; set; }
}

/// <summary>举报受理命令。举报只进人工复核队列，绝不自动封禁。</summary>
public sealed class ReportCommand
{
    public string Host { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? Reason { get; set; }

    public string? Contact { get; set; }

    public string? ClientIp { get; set; }
}

public sealed class BanResult
{
    public bool Success { get; set; }

    public string? Error { get; set; }

    public BanRecord? Record { get; set; }

    public int AffectedHosts { get; set; }

    public int DisconnectedClients { get; set; }

    /// <summary>非空表示请求的级别被安全策略降级了。</summary>
    public string? DowngradedFrom { get; set; }

    public static BanResult Fail(string error) => new() { Success = false, Error = error };
}
