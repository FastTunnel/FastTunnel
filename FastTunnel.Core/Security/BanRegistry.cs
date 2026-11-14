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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Security;

/// <summary>
/// 三级黑名单（token / host / account）。内存判定 + JSON 状态文件持久化，
/// 保证「封禁不会因进程重启而失效」。这是单机持久化，不是审计级防篡改存储，
/// 多实例部署需替换为共享存储，见合规方案 §11。
/// </summary>
public sealed class BanRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, BanRecord> records = new(StringComparer.Ordinal);
    private readonly ILogger<BanRegistry> logger;
    private readonly string? stateFile;
    private readonly object fileLock = new();

    public BanRegistry(ILogger<BanRegistry> logger, IOptionsMonitor<DefaultServerConfig> options)
        : this(logger, options?.CurrentValue?.Abuse?.Ban?.StateFile)
    {
    }

    /// <summary>测试用：传 null / 空字符串可关闭文件持久化。</summary>
    public BanRegistry(ILogger<BanRegistry> logger, string? stateFile)
    {
        this.logger = logger;
        this.stateFile = string.IsNullOrWhiteSpace(stateFile) ? null : Path.GetFullPath(stateFile);
        Load();
    }

    /// <summary>
    /// 主体规范化。token 只保存 SHA-256 前 8 字节的指纹：黑名单文件里不落明文凭据，
    /// 但同样的 token 仍能稳定命中。host 统一小写、去尾点。
    /// </summary>
    public static string NormalizeSubject(BanSubjectType type, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return string.Empty;
        }

        var value = subject.Trim();
        return type switch
        {
            BanSubjectType.Token => Fingerprint(value),
            BanSubjectType.Host => value.TrimEnd('.').ToLowerInvariant(),
            _ => value.ToLowerInvariant(),
        };
    }

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string Key(BanSubjectType type, string normalized) => $"{(int)type}|{normalized}";

    public bool IsTokenBanned(string? token) => IsActive(BanSubjectType.Token, token);

    public bool IsHostBanned(string? host) => IsActive(BanSubjectType.Host, host);

    public bool IsAccountBanned(string? account) => IsActive(BanSubjectType.Account, account);

    private bool IsActive(BanSubjectType type, string? subject)
    {
        var normalized = NormalizeSubject(type, subject);
        if (normalized.Length == 0)
        {
            return false;
        }

        return records.TryGetValue(Key(type, normalized), out var record)
               && record.IsActive(DateTimeOffset.UtcNow);
    }

    /// <summary>连接层拦截用：host 优先，其次按会话 token。</summary>
    public BanRecord? Match(string? host, string? token) => Match(host, token, null);

    /// <summary>
    /// 连接层拦截用：host → token → account。account 只有在一户一 token 落地后才有可靠来源
    /// （<c>TunnelClient.Subject</c>），传 null 时行为与旧版一致。
    /// </summary>
    public BanRecord? Match(string? host, string? token, string? account)
    {
        foreach (var (type, raw) in new[]
                 {
                     (BanSubjectType.Host, host),
                     (BanSubjectType.Token, token),
                     (BanSubjectType.Account, account),
                 })
        {
            var normalized = NormalizeSubject(type, raw);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (records.TryGetValue(Key(type, normalized), out var record)
                && record.IsActive(DateTimeOffset.UtcNow))
            {
                return record;
            }
        }

        return null;
    }

    public BanRecord Add(BanRecord record)
    {
        record.Subject = NormalizeSubject(record.SubjectType, record.Subject);
        records[Key(record.SubjectType, record.Subject)] = record;
        Save();
        return record;
    }

    /// <summary>
    /// 把一次熔断关联到的全部域名一并拉黑。熔断会摘掉 WebList 映射，
    /// 没有这些记录的话，摘映射后 Host 就再也匹配不到封禁，451 会退化成 404。
    /// </summary>
    public int AddHosts(string groupId, IEnumerable<string> hosts, BanRecord template)
    {
        var added = 0;
        foreach (var host in hosts)
        {
            var normalized = NormalizeSubject(BanSubjectType.Host, host);
            var key = Key(BanSubjectType.Host, normalized);
            if (normalized.Length == 0 || records.ContainsKey(key))
            {
                continue;
            }

            records[key] = new BanRecord
            {
                GroupId = groupId,
                SubjectType = BanSubjectType.Host,
                Subject = normalized,
                Level = template.Level,
                ReviewState = template.ReviewState,
                RuleId = template.RuleId,
                EvidenceId = template.EvidenceId,
                Score = template.Score,
                Reason = template.Reason,
                Operator = template.Operator,
                ExpiresAt = template.ExpiresAt,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            added++;
        }

        if (added > 0)
        {
            Save();
        }

        return added;
    }

    public bool TryGet(string id, out BanRecord record)
    {
        record = records.Values.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        return record != null;
    }

    /// <summary>解封一条主体记录，并连带清掉同一次熔断产生的关联域名记录。</summary>
    public int RemoveGroup(string groupId)
    {
        var removed = 0;
        foreach (var pair in records.ToArray())
        {
            if (string.Equals(pair.Value.GroupId, groupId, StringComparison.Ordinal)
                && records.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            Save();
        }

        return removed;
    }

    public bool Remove(BanSubjectType type, string subject)
    {
        var normalized = NormalizeSubject(type, subject);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (!records.TryRemove(Key(type, normalized), out var removed))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(removed.GroupId))
        {
            RemoveGroup(removed.GroupId);
        }

        Save();
        return true;
    }

    /// <summary>
    /// 人工复核。驳回视为误封，整组（含关联域名）立即解除；
    /// 确认且要求升级时才允许把 Level 提到 Permanent——自动系统没有这条路径。
    /// </summary>
    public BanRecord? Review(string id, ReviewState state, string reviewer, string? note, bool elevate)
    {
        if (!TryGet(id, out var record))
        {
            return null;
        }

        if (state == ReviewState.Rejected)
        {
            RemoveGroup(record.GroupId);
            return record;
        }

        // 复核结论覆盖整组：否则关联域名记录会一直留在待复核队列里
        var now = DateTimeOffset.UtcNow;
        foreach (var item in records.Values.Where(x => string.Equals(x.GroupId, record.GroupId, StringComparison.Ordinal)))
        {
            item.ReviewState = state;
            item.ReviewedBy = reviewer;
            item.ReviewedAt = now;
            item.ReviewNote = note;
            if (elevate && state == ReviewState.Confirmed)
            {
                item.Level = BanLevel.Permanent;
            }
        }

        Save();
        return record;
    }

    public IReadOnlyList<BanRecord> Snapshot()
        => records.Values.OrderByDescending(x => x.CreatedAt).ToArray();

    public IReadOnlyList<BanRecord> PendingReviews()
        => Snapshot().Where(x => x.ReviewState == ReviewState.Pending).ToArray();

    private void Load()
    {
        if (stateFile == null || !File.Exists(stateFile))
        {
            return;
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<BanRecord>>(File.ReadAllText(stateFile), JsonOptions);
            if (list == null)
            {
                return;
            }

            foreach (var record in list)
            {
                if (string.IsNullOrWhiteSpace(record.Subject))
                {
                    continue;
                }

                // 文件里的 Subject 已经是规范化值（token 是指纹），不能再 Hash 一次
                record.Subject = record.Subject.Trim();
                records[Key(record.SubjectType, record.Subject)] = record;
            }

            logger.LogInformation("已恢复 {Count} 条封禁记录：{File}", records.Count, stateFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "封禁状态读取失败，本次启动以空黑名单开始：{File}", stateFile);
        }
    }

    private void Save()
    {
        if (stateFile == null)
        {
            return;
        }

        lock (fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(stateFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = stateFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Snapshot(), JsonOptions), Encoding.UTF8);
                File.Move(temp, stateFile, true);
            }
            catch (Exception ex)
            {
                // 文件失败不能让内存封禁失效，只告警
                logger.LogWarning(ex, "封禁状态持久化失败（内存黑名单仍生效）：{File}", stateFile);
            }
        }
    }
}
