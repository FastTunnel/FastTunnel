// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Security;

/// <summary>
/// 一条审计 / 留证记录。字段对齐合规方案 §6.5；token 只存指纹，
/// Cookie、Authorization 等凭据头从源头就不进入 <c>IFastTunnelFeature.Headers</c>。
/// </summary>
public sealed class AuditEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string Action { get; set; } = string.Empty;

    public string? ClientIp { get; set; }

    public string? Host { get; set; }

    public string? Path { get; set; }

    public string? Method { get; set; }

    public string? TokenFingerprint { get; set; }

    public string? BanId { get; set; }

    public string? GroupId { get; set; }

    public string? RuleId { get; set; }

    public double Score { get; set; }

    public string? EvidenceId { get; set; }

    public string? Level { get; set; }

    public string? Operator { get; set; }

    public string? Detail { get; set; }
}

/// <summary>
/// 内存环形缓冲 + JSONL 追加文件。检索走内存，长期留存与 6 个月归档由部署层的
/// 日志轮转 / 备份保证（见合规方案 §6.5、§11）；这里不做证据链签名。
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ConcurrentQueue<AuditEntry> entries = new();
    private readonly ILogger<AuditLog> logger;
    private readonly string? filePath;
    private readonly int capacity;
    private readonly object fileLock = new();

    public AuditLog(ILogger<AuditLog> logger, IOptionsMonitor<DefaultServerConfig> options)
        : this(logger, options?.CurrentValue?.Abuse?.Audit)
    {
    }

    /// <summary>测试用：options 为 null 或 FilePath 为空时关闭文件写入。</summary>
    public AuditLog(ILogger<AuditLog> logger, DefaultServerConfig.AuditOptions? options)
    {
        this.logger = logger;
        capacity = Math.Max(100, options?.MemoryCapacity ?? 10000);
        var path = options?.FilePath;
        filePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    public void Write(AuditEntry entry)
    {
        entries.Enqueue(entry);
        while (entries.Count > capacity && entries.TryDequeue(out _))
        {
            // 丢弃最旧的内存记录；文件里仍有完整历史
        }

        if (filePath == null)
        {
            return;
        }

        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "审计日志落盘失败（内存记录仍在）：{File}", filePath);
            }
        }
    }

    /// <summary>倒序分页检索，可按 host / action 过滤。</summary>
    public IReadOnlyList<AuditEntry> Query(int skip = 0, int take = 100, string? host = null, string? action = null)
    {
        var query = entries.ToArray().Reverse().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(host))
        {
            query = query.Where(x => string.Equals(x.Host, host.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => string.Equals(x.Action, action.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return query.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 1000)).ToArray();
    }
}
