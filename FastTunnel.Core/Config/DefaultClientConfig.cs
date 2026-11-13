// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;
using FastTunnel.Core.Models;

namespace FastTunnel.Core.Config;

public class DefaultClientConfig : IClientConfig
{
    public SuiDaoServer Server { get; set; }

    public string Token { get; set; }

    public IEnumerable<WebConfig> Webs { get; set; }

    public IEnumerable<ForwardConfig> Forwards { get; set; }

    /// <summary>
    /// 是否启用多路复用（默认开启）。开启后只与服务端建立一条长连接，
    /// 所有转发在该连接上开流；服务端不支持时会自动回退。
    /// </summary>
    public bool EnableMux { get; set; } = true;

    /// <summary>
    /// 多路复用数据通道的承载方式：
    /// - <c>tcp</c>（默认）：直接一条 TCP（可套 TLS），开销最小；
    /// - <c>websocket</c>：走 HTTP Upgrade（<c>/mux</c>），能穿过只放行 HTTP 的防火墙，
    ///   也能被 nginx 按路径反代，代价是每条消息多几字节 WS 头。
    /// </summary>
    public string MuxTransport { get; set; } = "tcp";
}
