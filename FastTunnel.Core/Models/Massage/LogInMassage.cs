// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;

namespace FastTunnel.Core.Models.Massage;

public class LogInMassage : TunnelMassage
{
    /// <summary>
    /// web穿透隧道列表
    /// </summary>
    public IEnumerable<WebConfig> Webs { get; set; }

    /// <summary>
    /// 端口转发隧道列表
    /// </summary>
    public IEnumerable<ForwardConfig> Forwards { get; set; }

    /// <summary>
    /// 客户端支持的控制面协议版本。服务端据此决定下发 JSON 还是旧版竖线文本，
    /// 保证新旧客户端可以连接同一个服务端。
    /// </summary>
    public int ProtocolVersion { get; set; }

    /// <summary>
    /// 客户端为本次会话随机生成的多路复用标识。客户端随后会用 <c>MUX /{MuxId}</c> 建立
    /// 一条长连接，服务端据此把该连接绑定到当前登录会话。为空表示客户端不支持多路复用。
    /// </summary>
    public Guid MuxId { get; set; }
}
