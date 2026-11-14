// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;

namespace FastTunnel.Core.Config;

/// <summary>
/// 「一户一 token」的服务端条目。每个用户一条独立凭据、一个稳定主体 Id，
/// 封禁 / 审计 / 带宽统计都按 <see cref="Id"/> 归属，不再靠共享静态串反查。
/// </summary>
public sealed class ClientCredentialOptions
{
    /// <summary>主体标识（账号 / 租户），必须唯一。建议用不含敏感信息的稳定 ID。</summary>
    public string? Id { get; set; }

    /// <summary>该主体专用的 token，必须唯一。客户端填在 <c>FastTunnel:Token</c>。</summary>
    public string? Token { get; set; }

    /// <summary>可选：限定该主体只能使用这个 SubDomain（单个标签，不含根域名）。</summary>
    public string? SubDomain { get; set; }

    /// <summary>
    /// 可选：允许登记的主机名白名单，支持精确域名与 <c>.suffix</c>（含子域）。
    /// 非空时，子域名映射与自定义域名都必须命中；为空则不限制。
    /// </summary>
    public string[] AllowedHosts { get; set; } = Array.Empty<string>();

    /// <summary>可选：该主体的带宽上限（字节/秒）。null 表示继承全局配置；0 表示显式不限速。</summary>
    public long? BandwidthLimit { get; set; }

    /// <summary>停用单个主体但保留配置，默认启用。</summary>
    public bool Enabled { get; set; } = true;
}
