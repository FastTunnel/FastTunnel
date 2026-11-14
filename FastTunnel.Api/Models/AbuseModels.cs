// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

namespace FastTunnel.Api.Models
{
    /// <summary>封禁请求。SubjectType: token / host / account。</summary>
    public class BanRequest
    {
        public string SubjectType { get; set; } = "host";

        public string Subject { get; set; }

        public string Reason { get; set; }

        public string RuleId { get; set; }

        public double Score { get; set; }

        public string EvidenceId { get; set; }

        /// <summary>留空表示不自动解封；违法内容不应因超时自动恢复。</summary>
        public int? ExpiresMinutes { get; set; }

        /// <summary>observe / throttle / block / permanent。permanent 会被降级为 block。</summary>
        public string Level { get; set; } = "block";

        // 证据字段，随审计日志留存
        public string ClientIp { get; set; }

        public string Host { get; set; }

        public string Path { get; set; }

        public string Method { get; set; }

        /// <summary>原始 token，只在内存里用于生成指纹，不写日志。</summary>
        public string Token { get; set; }
    }

    public class UnbanRequest
    {
        public string SubjectType { get; set; } = "host";

        public string Subject { get; set; }

        public string Reason { get; set; }
    }

    public class ReviewRequest
    {
        public string BanId { get; set; }

        /// <summary>confirmed / rejected。rejected 视为误封，整组立即解封。</summary>
        public string State { get; set; }

        public string Note { get; set; }

        /// <summary>确认后是否升级为永久封禁。这是唯一的永封入口。</summary>
        public bool Elevate { get; set; }
    }

    /// <summary>对外举报入口。举报只进人工复核队列，不自动封禁。</summary>
    public class ReportRequest
    {
        public string Host { get; set; }

        public string Path { get; set; }

        public string Reason { get; set; }

        public string Contact { get; set; }
    }
}
