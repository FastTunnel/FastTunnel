// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using FastTunnel.Api.Models;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FastTunnel.Api.Controllers
{
    /// <summary>
    /// 违规处置接口。除 Report（对外举报）外全部要求 JWT。
    /// 自动系统只能创建 Block 级熔断；永久封禁与上报必须走 Review 人工确认。
    /// </summary>
    public class AbuseController : BaseController
    {
        private readonly BanService banService;
        private readonly BanRegistry registry;
        private readonly AuditLog audit;
        private readonly IOptionsMonitor<DefaultServerConfig> options;

        public AbuseController(
            BanService banService, BanRegistry registry, AuditLog audit,
            IOptionsMonitor<DefaultServerConfig> options)
        {
            this.banService = banService;
            this.registry = registry;
            this.audit = audit;
            this.options = options;
        }

        /// <summary>熔断：按 token / host 立即停止传输、撤销映射、断开会话并留证。</summary>
        [HttpPost]
        public ApiResponse Ban([FromBody] BanRequest request)
        {
            if (request == null || !TryParseSubjectType(request.SubjectType, out var subjectType))
            {
                return Fail("SubjectType 必须是 token / host / account");
            }

            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                return Fail("Subject 不能为空");
            }

            var command = new BanCommand
            {
                SubjectType = subjectType,
                Subject = request.Subject,
                Reason = request.Reason,
                RuleId = request.RuleId,
                Score = request.Score,
                EvidenceId = request.EvidenceId,
                ExpiresAt = request.ExpiresMinutes is > 0
                    ? DateTimeOffset.UtcNow.AddMinutes(request.ExpiresMinutes.Value)
                    : null,
                RequestedLevel = ParseLevel(request.Level),
                Operator = Operator(),
                ClientIp = request.ClientIp,
                Host = request.Host,
                Path = request.Path,
                Method = request.Method,
                TokenFingerprint = BanRegistry.Fingerprint(request.Token),
            };

            var result = banService.Ban(command);
            if (!result.Success)
            {
                return Fail(result.Error ?? "封禁失败");
            }

            ApiResponse.data = new
            {
                result.Record,
                result.AffectedHosts,
                result.DisconnectedClients,
                result.DowngradedFrom,
            };
            return ApiResponse;
        }

        /// <summary>解封：主体与同组关联域名一起移除。</summary>
        [HttpPost]
        public ApiResponse Unban([FromBody] UnbanRequest request)
        {
            if (request == null || !TryParseSubjectType(request.SubjectType, out var subjectType))
            {
                return Fail("SubjectType 必须是 token / host / account");
            }

            var result = banService.Unban(subjectType, request?.Subject ?? string.Empty, Operator(), request?.Reason);
            if (!result.Success)
            {
                return Fail(result.Error ?? "解封失败");
            }

            ApiResponse.data = new { Unbanned = true };
            return ApiResponse;
        }

        /// <summary>人工复核：驳回即整组解封，确认后可升级为永久封禁。</summary>
        [HttpPost]
        public ApiResponse Review([FromBody] ReviewRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BanId))
            {
                return Fail("BanId 不能为空");
            }

            var state = request.State?.Trim().ToLowerInvariant() switch
            {
                "confirmed" => ReviewState.Confirmed,
                "rejected" => ReviewState.Rejected,
                _ => ReviewState.Pending,
            };

            var result = banService.Review(request.BanId, state, Operator(), request.Note, request.Elevate);
            if (!result.Success)
            {
                return Fail(result.Error ?? "复核失败");
            }

            ApiResponse.data = result.Record;
            return ApiResponse;
        }

        /// <summary>
        /// 对外举报入口（《网络安全法》第 51 条要求公布举报方式并及时受理）。
        /// 匿名开放，只落库进待复核队列，绝不自动封禁。
        /// </summary>
        [AllowAnonymous]
        [HttpPost]
        public ApiResponse Report([FromBody] ReportRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Host))
            {
                return Fail("Host 不能为空");
            }

            if ((request.Host?.Length ?? 0) > 253
                || (request.Reason?.Length ?? 0) > 2000
                || (request.Path?.Length ?? 0) > 2048
                || (request.Contact?.Length ?? 0) > 256)
            {
                return Fail("举报内容超长");
            }

            var reportId = banService.Report(new ReportCommand
            {
                Host = request.Host,
                Path = request.Path,
                Reason = request.Reason,
                Contact = request.Contact,
                ClientIp = ResolveClientIp(),
            });

            ApiResponse.data = new { ReportId = reportId, State = "pending_review" };
            return ApiResponse;
        }

        /// <summary>当前生效与历史的封禁 / 观察 / 举报记录。</summary>
        [HttpGet]
        public ApiResponse List()
        {
            ApiResponse.data = registry.Snapshot();
            return ApiResponse;
        }

        /// <summary>待人工复核队列。</summary>
        [HttpGet]
        public ApiResponse Reviews()
        {
            ApiResponse.data = registry.PendingReviews();
            return ApiResponse;
        }

        /// <summary>审计检索（倒序分页）。</summary>
        [HttpGet]
        public ApiResponse Audit(int skip = 0, int take = 100, string host = null, string action = null)
        {
            ApiResponse.data = audit.Query(skip, take, host, action);
            return ApiResponse;
        }

        private string Operator() => User?.Identity?.Name ?? "api";

        private string? ResolveClientIp()
        {
            var remote = HttpContext.Connection.RemoteIpAddress;
            var headers = Request.Headers.ToDictionary(
                x => x.Key,
                x => x.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

            return ClientIpResolver.Resolve(remote, headers, options.CurrentValue.TrustedProxyIps)?.ToString();
        }

        private ApiResponse Fail(string message)
        {
            ApiResponse.errorCode = ErrorCodeEnum.Exception;
            ApiResponse.errorMessage = message;
            return ApiResponse;
        }

        private static bool TryParseSubjectType(string? value, out BanSubjectType type)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "token":
                    type = BanSubjectType.Token;
                    return true;
                case "host":
                case "domain":
                    type = BanSubjectType.Host;
                    return true;
                case "account":
                    type = BanSubjectType.Account;
                    return true;
                default:
                    type = BanSubjectType.Host;
                    return false;
            }
        }

        private static BanLevel ParseLevel(string? value)
            => value?.Trim().ToLowerInvariant() switch
            {
                "observe" => BanLevel.Observe,
                "throttle" => BanLevel.Throttle,
                "permanent" => BanLevel.Permanent,
                _ => BanLevel.Block,
            };
    }
}
