// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Net;
using FastTunnel.Core.Models;

namespace FastTunnel.Core.Forwarder.Kestrel.Features;

internal interface IFastTunnelFeature
{
    public WebInfo MatchWeb { get; set; }

    public string Method { get; set; }

    public string Host { get; set; }

    /// <summary>请求行的路径部分（按原样保留，可能含 query）。</summary>
    public string? Path { get; set; }

    public Guid MessageId { get; set; }

    /// <summary>
    /// 保留下来的审计请求头，键统一小写。Cookie / Authorization 等凭据头不入库，
    /// 避免把用户凭据带进审计日志与旁路送审。
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; set; }

    /// <summary>还原后的真实访客 IP；无法还原时为 TCP 对端 IP。</summary>
    public IPAddress? ClientIp { get; set; }
}
