// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;

namespace FastTunnel.Core.Config;

public class DefaultServerConfig : IServerConfig
{
    public string WebDomain { get; set; }

    /// <summary>
    /// 自定义域名（客户端 Webs[].WWW）黑名单。支持精确域名和以点开头的后缀两种写法，
    /// 例如 <c>"bad.example.com"</c> 与 <c>".example.com"</c>（后者含全部子域）。
    /// 命中后拒绝登记映射。为空表示不限制。
    /// </summary>
    public string[] WebDomainBlacklist { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 访问来源 IP 白名单，只作用于公网访客的 HTTP 请求。支持单个 IP 与 CIDR
    /// （如 <c>"192.168.0.0/24"</c>）。为空表示不限制。
    /// 判定用的是 <c>ClientIpResolver</c> 还原出的真实访客 IP，不是可伪造的 XFF。
    /// </summary>
    public string[] WebAllowAccessIps { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 可信反向代理 IP / CIDR。只有来自这些地址的连接才会采信转发类请求头；
    /// 为空表示一律以 TCP 对端 IP 为准。不要配置通配网段。
    /// </summary>
    public string[] TrustedProxyIps { get; set; } = Array.Empty<string>();

    public bool EnableForward { get; set; }

    /// <summary>
    /// TCP/UDP 转发的服务端端口白名单。条目 <c>{ Single }</c> 放行单端口，
    /// <c>{ Start, End }</c> 放行闭区间。为空表示不限制；配置了条目时，
    /// 白名单外的端口一律拒绝登记。与域名黑名单配对使用：HTTP 靠域名收口，TCP/UDP 靠端口收口。
    /// </summary>
    public List<AllowPortOptions> AllowPorts { get; set; } = new();

    /// <summary>
    /// 单个客户端最多可占用的转发端口数（按去重后的 RemotePort 计）。0 表示不限制。
    /// 超出后新增的端口登记会被拒绝，不影响已建立的转发。
    /// </summary>
    public int MaxPortsPerClient { get; set; }

    /// <summary>
    /// 是否启用多路复用。开启后，客户端会额外建立一条长连接，所有 HTTP / TCP 转发
    /// 都在该连接上开流，不再为每条公网连接新建一条 TCP。默认开启。
    /// </summary>
    public bool EnableMux { get; set; } = true;

    /// <summary>
    /// 客户端通道的 TLS 配置。公网 HTTP 访问不受影响（同一端口按首字节分流）。
    /// </summary>
    public TlsOptions Tls { get; set; } = new();

    public class TlsOptions
    {
        /// <summary>
        /// 是否允许客户端用 TLS 连接本服务端。默认开启。
        /// 关闭后客户端只能用 Protocol=ws 明文接入。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 证书文件（pfx/pem）。留空则使用 <see cref="AutoGenerate"/> 自动生成的自签证书。
        /// </summary>
        public string CertFile { get; set; }

        public string CertPassword { get; set; }

        /// <summary>
        /// 未配置证书时自动生成自签证书，并持久化到 config/ft-server.pfx。
        /// 默认开启，避免自建用户被 TLS 门槛劝退。
        /// </summary>
        public bool AutoGenerate { get; set; } = true;
    }

    [Obsolete("由Tokens替换")]
    public string Token { get; set; }

    /// <summary>
    /// 旧版共享凭据列表。所有客户端共用一个 token，封禁无法定位到人，
    /// 建议改用 <see cref="Clients"/> 的一户一 token；保留仅为了兼容既有部署。
    /// </summary>
    public List<string> Tokens { get; set; }

    /// <summary>一户一 token：每个主体一条独立凭据、一个稳定 Id。</summary>
    public List<ClientCredentialOptions> Clients { get; set; } = new();

    /// <summary>
    /// 是否强制同一主体只允许一个在线会话。开启后新登录会顶掉旧会话，
    /// 防止一条凭据被多处同时使用。旧版共享 Tokens 不参与此判定。
    /// </summary>
    public bool EnforceSingleSession { get; set; } = true;

    /// <summary>带宽限速规则（字节/秒）。</summary>
    public BandwidthOptions Bandwidth { get; set; } = new();

    /// <summary>运维端点（/healthz、/metrics）行为。</summary>
    public OpsOptions Ops { get; set; } = new();

    /// <summary>违规管控：封禁状态持久化与审计留痕。</summary>
    public AbuseOptions Abuse { get; set; } = new();

    public class AbuseOptions
    {
        public BanOptions Ban { get; set; } = new();

        public AuditOptions Audit { get; set; } = new();
    }

    public class BanOptions
    {
        /// <summary>
        /// 封禁状态文件（相对进程工作目录）。留空则只在内存里生效，
        /// 进程重启后封禁会失效，生产环境不建议留空。
        /// </summary>
        public string StateFile { get; set; } = "config/bans.json";
    }

    public class AuditOptions
    {
        /// <summary>JSONL 审计文件。留空则只保留内存缓冲。留存 ≥ 6 个月由部署层日志归档保证。</summary>
        public string FilePath { get; set; } = "config/audit.jsonl";

        /// <summary>内存检索窗口大小，超出后只保留最近的记录（文件里仍有完整历史）。</summary>
        public int MemoryCapacity { get; set; } = 10000;
    }

    public ApiOptions Api { get; set; }

    public class ApiOptions
    {
        public JWTOptions JWT { get; set; }

        public Account[] Accounts { get; set; }
    }

    public class JWTOptions
    {
        public int ClockSkew { get; set; }

        public string ValidAudience { get; set; }

        public string ValidIssuer { get; set; }

        public string IssuerSigningKey { get; set; }

        public int Expires { get; set; }
    }

    public class Account
    {
        public string Name { get; set; }

        public string Password { get; set; }
    }
}
