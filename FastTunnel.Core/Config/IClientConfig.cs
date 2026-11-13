// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;
using FastTunnel.Core.Models;

namespace FastTunnel.Core.Config;

public interface IClientConfig
{
    public SuiDaoServer Server { get; set; }

    public IEnumerable<WebConfig> Webs { get; set; }

    public IEnumerable<ForwardConfig> Forwards { get; set; }
}

public class SuiDaoServer
{
    /// <summary>
    /// 传输协议：wss（TLS，默认）或 ws（明文，仅限受信内网）。
    /// 两种方式连的都是同一个端口，服务端按首字节区分。
    /// </summary>
    public string Protocol { get; set; } = "wss";

    public string ServerAddr { get; set; }

    public int ServerPort { get; set; }

    /// <summary>
    /// 服务端证书的 SHA-256 指纹（十六进制，分隔符可有可无）。
    /// 配置后只信任该证书，比信任系统 CA 更适合自签场景。
    /// 服务端启动日志会打印自己的指纹。
    /// </summary>
    public string CertificateFingerprint { get; set; }

    /// <summary>
    /// 跳过服务端证书校验：仍然加密，但不验证对端身份，无法防中间人。
    /// 仅用于开发或自建自签证书，生产环境请改用 <see cref="CertificateFingerprint"/>。
    /// </summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>是否启用 TLS。ws 表示明文。</summary>
    public bool UseTls => !string.Equals(Protocol, "ws", StringComparison.OrdinalIgnoreCase);
}
