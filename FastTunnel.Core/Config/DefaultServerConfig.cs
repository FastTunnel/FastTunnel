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

    public string[] WebAllowAccessIps { get; set; }

    public bool EnableForward { get; set; }

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

    public List<string> Tokens { get; set; }

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
