// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Linq;
using System.Net;
using FastTunnel.Api.Models;
using FastTunnel.Core.Config;
using FastTunnel.Core.Forwarder.Kestrel;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FastTunnel.Api.Controllers
{
    /// <summary>
    /// 入网设备台账接口。除 Enroll（凭一次性入网码兑换，天然匿名）外全部要求 JWT。
    /// 明文 token 只在 Enroll 响应里出现一次，服务端任何时刻不落盘、也不再返回。
    /// </summary>
    public class PeersController : BaseController
    {
        private readonly PeerRegistry peers;
        private readonly AuditLog audit;
        private readonly IOptionsMonitor<DefaultServerConfig> options;

        public PeersController(
            PeerRegistry peers, AuditLog audit, IOptionsMonitor<DefaultServerConfig> options)
        {
            this.peers = peers;
            this.audit = audit;
            this.options = options;
        }

        /// <summary>
        /// 客户端用入网码兑换长期凭据。码是 HMAC 自包含且单次使用，
        /// 猜中概率可忽略，因此这里只做事后审计，不额外增加鉴权。
        /// </summary>
        [AllowAnonymous]
        [HttpPost]
        public ApiResponse Enroll([FromBody] PeerEnrollRequest request)
        {
            var key = options.CurrentValue.P2PInviteKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return Fail("服务端未配置 FastTunnel:P2PInviteKey，入网功能已停用");
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
            {
                return Fail("Code 不能为空");
            }

            if (request.Code.Length > 200 || (request.Hostname?.Length ?? 0) > 253)
            {
                return Fail("入网码或主机名过长");
            }

            var remote = ResolveClientIp();
            peers.RefreshFromDisk();
            var credential = peers.Redeem(key, request.Code, request.Hostname);
            if (credential == null)
            {
                WriteAudit("peer.enroll_denied", null, remote, "入网码无效、已过期或已被使用");
                return Fail("入网码无效、已过期或已被使用");
            }

            WriteAudit("peer.enroll", credential.Id, remote, "设备入网");
            ApiResponse.data = new
            {
                credential.Id,
                credential.Name,
                Token = credential.Token,
            };
            return ApiResponse;
        }

        /// <summary>签发一次性入网码。明文码只返回这一次，台账里只留指纹。</summary>
        [HttpPost]
        public ApiResponse Create([FromBody] PeerTicketRequest request)
        {
            var config = options.CurrentValue;
            if (string.IsNullOrWhiteSpace(config.P2PInviteKey))
            {
                return Fail("请先配置 FastTunnel:P2PInviteKey");
            }

            var minutes = request?.Minutes ?? config.P2PEnrollment?.EnrollMinutes ?? PeerEnrollment.DefaultMinutes;
            if (minutes is < 1 or > PeerEnrollment.MaxMinutes)
            {
                return Fail($"有效分钟数应为 1–{PeerEnrollment.MaxMinutes}");
            }

            IssuedTicket issued;
            try
            {
                peers.RefreshFromDisk();
                issued = peers.CreateTicket(config.P2PInviteKey, request?.Name, minutes);
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message);
            }

            WriteAudit("peer.ticket_create", issued.Id.ToString("N"), ResolveClientIp(), $"截止 {issued.Expires:O}");
            ApiResponse.data = new { issued.Id, issued.Code, issued.Expires };
            return ApiResponse;
        }

        /// <summary>已入网设备列表（只含指纹，不含任何明文凭据）。</summary>
        [HttpGet]
        public ApiResponse List()
        {
            peers.RefreshFromDisk();
            ApiResponse.data = peers.Peers.Select(x => new
            {
                x.Id,
                x.Name,
                x.Enabled,
                x.EnrolledAt,
                LastSeen = peers.LastSeenOf(x.Id) ?? x.LastSeen,
                x.TokenFingerprint,
            }).ToArray();
            return ApiResponse;
        }

        /// <summary>吊销设备：凭据立即失效，进行中的会话在下一次授权复检时断开。</summary>
        [HttpPost]
        public ApiResponse Revoke([FromBody] PeerSubjectRequest request)
            => SetState(request, revoke: true);

        /// <summary>恢复一台已吊销设备，凭据不变。</summary>
        [HttpPost]
        public ApiResponse Restore([FromBody] PeerSubjectRequest request)
            => SetState(request, revoke: false);

        /// <summary>删除设备记录，需要重新签发入网码才能再次入网。</summary>
        [HttpPost]
        public ApiResponse Remove([FromBody] PeerSubjectRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Id))
            {
                return Fail("Id 不能为空");
            }

            peers.RefreshFromDisk();
            if (!peers.Remove(request.Id))
            {
                return Fail("未找到该设备");
            }

            WriteAudit("peer.remove", request.Id, ResolveClientIp(), request.Reason);
            ApiResponse.data = new { Removed = true };
            return ApiResponse;
        }

        /// <summary>待用入网码（不含明文码）。</summary>
        [HttpGet]
        public ApiResponse Tickets()
        {
            peers.RefreshFromDisk();
            ApiResponse.data = peers.PendingTickets();
            return ApiResponse;
        }

        private ApiResponse SetState(PeerSubjectRequest request, bool revoke)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Id))
            {
                return Fail("Id 不能为空");
            }

            peers.RefreshFromDisk();
            var changed = revoke ? peers.Revoke(request.Id) : peers.Restore(request.Id);
            if (!changed)
            {
                return Fail(revoke ? "设备已是吊销状态或不存在" : "设备已是启用状态或不存在");
            }

            WriteAudit(revoke ? "peer.revoke" : "peer.restore", request.Id, ResolveClientIp(), request.Reason);
            ApiResponse.data = new { Changed = true };
            return ApiResponse;
        }

        private void WriteAudit(string action, string? subject, IPAddress? clientIp, string? detail)
        {
            audit.Write(new AuditEntry
            {
                Action = action,
                ClientIp = clientIp?.ToString(),
                Path = HttpContext.Request.Path,
                Method = HttpMethods.Post,
                Operator = User?.Identity?.Name ?? "anonymous",
                Detail = string.IsNullOrEmpty(detail) ? $"subject={subject}" : $"subject={subject} {detail}",
            });
        }

        private IPAddress? ResolveClientIp()
        {
            var remote = HttpContext.Connection.RemoteIpAddress;
            var headers = Request.Headers.ToDictionary(
                x => x.Key,
                x => x.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

            return ClientIpResolver.Resolve(remote, headers, options.CurrentValue.TrustedProxyIps);
        }

        private ApiResponse Fail(string message)
        {
            ApiResponse.errorCode = ErrorCodeEnum.Exception;
            ApiResponse.errorMessage = message;
            return ApiResponse;
        }
    }
}
