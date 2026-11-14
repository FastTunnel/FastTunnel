// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Security;

/// <summary>
/// 违规命中的处置编排。顺序不可颠倒（合规方案 §6.4）：
/// ① 立封禁标记 → ② 关联域名一并拉黑 → ③ 摘映射 / 停监听 / 关 mux / 断控制面
/// → ④ 保存证据与访问日志 → ⑤ 进入人工复核队列（日志告警）。
///
/// 自动系统只能做到「熔断 + 留证」：Ban 一律创建 Block 级别记录，
/// 永久封禁与上报必须经 <see cref="Review"/> 人工确认，复核接口是唯一升级路径。
/// </summary>
public sealed class BanService
{
    private readonly FastTunnelServer server;
    private readonly BanRegistry registry;
    private readonly AuditLog audit;
    private readonly ILogger<BanService> logger;

    public BanService(
        FastTunnelServer server, BanRegistry registry, AuditLog audit, ILogger<BanService> logger)
    {
        this.server = server;
        this.registry = registry;
        this.audit = audit;
        this.logger = logger;
    }

    public BanResult Ban(BanCommand command)
    {
        var normalized = BanRegistry.NormalizeSubject(command.SubjectType, command.Subject);
        if (normalized.Length == 0)
        {
            return BanResult.Fail("封禁主体不能为空");
        }

        var level = command.RequestedLevel;
        string? downgradedFrom = null;
        if (level == BanLevel.Permanent)
        {
            // 自动系统不得直接永封；必须由人工复核确认后升级
            level = BanLevel.Block;
            downgradedFrom = nameof(BanLevel.Permanent);
            logger.LogWarning("拒绝直接创建永久封禁，已降级为熔断：{Type}（主体指纹 {Subject}）",
                command.SubjectType, normalized);
        }

        var groupId = Guid.NewGuid().ToString("N");
        var primary = new BanRecord
        {
            GroupId = groupId,
            SubjectType = command.SubjectType,
            Subject = command.Subject,
            Level = level,
            ReviewState = ReviewState.Pending,
            RuleId = command.RuleId,
            Score = command.Score,
            EvidenceId = command.EvidenceId,
            Reason = command.Reason,
            Operator = command.Operator,
            ExpiresAt = command.ExpiresAt,
        };

        // ① 先立标记：此刻起所有新请求都会被 GuardMiddleware 拦成 451
        registry.Add(primary);

        // ② 收集受影响会话的所有域名并一并拉黑。
        //    摘掉 WebList 后 Host 不再有 MatchWeb，只能靠这些记录继续返回 451。
        var clients = FindAffectedClients(command.SubjectType, normalized).ToArray();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var webSnapshot = server.SnapshotWebs();
        foreach (var client in clients)
        {
            foreach (var web in webSnapshot)
            {
                if (ReferenceEquals(web.Client, client) && !string.IsNullOrEmpty(web.Host))
                {
                    hosts.Add(web.Host);
                }
            }
        }

        if (command.SubjectType == BanSubjectType.Host)
        {
            hosts.Add(normalized);
        }

        var affectedHosts = registry.AddHosts(groupId, hosts, primary);

        // ③ 断开：Logout 摘 WebList / ForwardList、停监听、关 mux，Abort 立即断控制面
        var disconnected = 0;
        foreach (var client in clients)
        {
            server.TerminateClient(client);
            disconnected++;
        }

        // ④ 留证
        audit.Write(NewEntry("ban", command, primary,
            $"hosts={hosts.Count};addedHosts={affectedHosts};disconnected={disconnected};"
            + "chain=mark->unmap->disconnect->evidence->review"));

        // ⑤ 复核队列由 ReviewState=Pending 天然形成；这里只发告警，不做自动上报
        logger.LogWarning(
            "熔断完成 BanId={BanId} Type={Type} Level={Level} Hosts={Hosts} Clients={Clients} RuleId={RuleId} EvidenceId={EvidenceId} Operator={Operator}，等待人工复核",
            primary.Id, primary.SubjectType, primary.Level, hosts.Count, disconnected,
            primary.RuleId, primary.EvidenceId, primary.Operator);

        return new BanResult
        {
            Success = true,
            Record = primary,
            AffectedHosts = affectedHosts,
            DisconnectedClients = disconnected,
            DowngradedFrom = downgradedFrom,
        };
    }

    /// <summary>解封：整组（主体 + 关联域名）一起移除，并写复核留痕。</summary>
    public BanResult Unban(BanSubjectType subjectType, string subject, string? reviewer, string? reason)
    {
        var normalized = BanRegistry.NormalizeSubject(subjectType, subject);
        if (normalized.Length == 0)
        {
            return BanResult.Fail("解封主体不能为空");
        }

        if (!registry.Remove(subjectType, subject))
        {
            return BanResult.Fail("未找到生效的封禁记录");
        }

        audit.Write(new AuditEntry
        {
            Action = "unban",
            Host = subjectType == BanSubjectType.Host ? normalized : null,
            TokenFingerprint = subjectType == BanSubjectType.Token ? normalized : null,
            Operator = reviewer,
            Detail = reason,
        });

        logger.LogInformation("解封完成 Type={Type} Operator={Operator} Reason={Reason}",
            subjectType, reviewer, reason);
        return new BanResult { Success = true };
    }

    /// <summary>人工复核。驳回即整组解封，确认后可升级为永久封禁。</summary>
    public BanResult Review(string id, ReviewState state, string reviewer, string? note, bool elevate)
    {
        if (state == ReviewState.Pending)
        {
            return BanResult.Fail("复核结果必须是 Confirmed 或 Rejected");
        }

        var record = registry.Review(id, state, reviewer, note, elevate);
        if (record == null)
        {
            return BanResult.Fail("未找到该封禁记录");
        }

        audit.Write(new AuditEntry
        {
            Action = "review",
            BanId = record.Id,
            GroupId = record.GroupId,
            Host = record.SubjectType == BanSubjectType.Host ? record.Subject : null,
            TokenFingerprint = record.SubjectType == BanSubjectType.Token ? record.Subject : null,
            Level = record.Level.ToString(),
            Operator = reviewer,
            Detail = $"state={state};elevate={elevate};note={note}",
        });

        return new BanResult { Success = true, Record = record };
    }

    /// <summary>举报受理：只记录并进入待复核队列，绝不自动封禁（方案 §9）。</summary>
    public string Report(ReportCommand command)
    {
        var host = BanRegistry.NormalizeSubject(BanSubjectType.Host, command.Host);
        var record = registry.Add(new BanRecord
        {
            SubjectType = BanSubjectType.Host,
            Subject = command.Host,
            Level = BanLevel.Observe,
            ReviewState = ReviewState.Pending,
            Reason = command.Reason,
            Operator = "report",
        });

        audit.Write(new AuditEntry
        {
            Action = "report",
            ClientIp = command.ClientIp,
            Host = host,
            Path = command.Path,
            BanId = record.Id,
            GroupId = record.GroupId,
            Detail = $"contact={command.Contact};reason={command.Reason}",
        });

        logger.LogInformation("收到举报 ReportId={Id} Host={Host}（未自动处置，等待人工复核）", record.Id, host);
        return record.Id;
    }

    private IEnumerable<Models.TunnelClient> FindAffectedClients(BanSubjectType type, string normalized)
    {
        var clients = server.SnapshotClients();

        if (type == BanSubjectType.Token)
        {
            return clients.Where(x => x.Token != null
                && string.Equals(BanRegistry.NormalizeSubject(BanSubjectType.Token, x.Token),
                    normalized, StringComparison.Ordinal));
        }

        if (type == BanSubjectType.Host)
        {
            return server.SnapshotWebs()
                .Where(x => string.Equals(
                    BanRegistry.NormalizeSubject(BanSubjectType.Host, x.Host),
                    normalized, StringComparison.Ordinal))
                .Select(x => x.Client)
                .Where(x => x != null)
                .Distinct()!;
        }

        // 一户一 token 落地后，Account 就是 Clients[].Id；按会话主体精确匹配。
        // legacy / anonymous 主体的 Id 不会与具名主体重名，不会被误伤。
        if (type == BanSubjectType.Account)
        {
            return clients.Where(x => x.HasNamedCredential
                && string.Equals(BanRegistry.NormalizeSubject(BanSubjectType.Account, x.Subject),
                    normalized, StringComparison.Ordinal));
        }

        return Enumerable.Empty<Models.TunnelClient>();
    }

    private static AuditEntry NewEntry(string action, BanCommand command, BanRecord record, string detail)
        => new()
        {
            Action = action,
            ClientIp = command.ClientIp,
            Host = command.Host,
            Path = command.Path,
            Method = command.Method,
            TokenFingerprint = command.TokenFingerprint,
            BanId = record.Id,
            GroupId = record.GroupId,
            RuleId = record.RuleId,
            Score = record.Score,
            EvidenceId = record.EvidenceId,
            Level = record.Level.ToString(),
            Operator = record.Operator,
            Detail = detail,
        };
}
