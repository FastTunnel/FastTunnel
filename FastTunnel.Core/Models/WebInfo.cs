// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.WebSockets;
using FastTunnel.Core.Protocol;

namespace FastTunnel.Core.Models;

public class WebInfo
{
    public WebSocket Socket { get; set; }

    public WebConfig WebConfig { get; set; }

    /// <summary>
    /// 所属客户端会话。多路复用已建立时优先走流，未建立时回退到 WebSocket 通知 + 回连。
    /// </summary>
    public TunnelClient Client { get; set; }

    /// <summary>
    /// 所属客户端的控制面协议版本，决定下发 SwapMsg 时用 JSON 还是旧版竖线文本。
    /// </summary>
    public int ProtocolVersion { get; set; } = ControlProtocol.LegacyVersion;

    internal void LogOut()
    {
        // TODO:退出登录
    }
}
