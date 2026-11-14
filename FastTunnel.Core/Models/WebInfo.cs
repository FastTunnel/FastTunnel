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
    /// <summary>
    /// 路由表里的完整主机名（小写、不含端口）。会话下线/封禁时据此精确摘除映射，
    /// 一个客户端声明多个 WWW 域名时每个域名对应一个独立的 WebInfo 实例。
    /// </summary>
    public string Host { get; set; }

    public WebSocket Socket { get; set; }

    public WebConfig WebConfig { get; set; }

    /// <summary>
    /// 所属客户端会话。多路复用已建立时优先走流，未建立时回退到 WebSocket 通知 + 回连。
    /// </summary>
    public TunnelClient Client { get; set; }

    /// <summary>
    /// 所在转发成员组（host → 组容器的回引）。转发链路据此上报成员成败，
    /// 驱动被动摘除；旧版单映射语义下同样非 null（单成员组）。
    /// </summary>
    public Server.WebHostGroup? GroupRef { get; set; }

    /// <summary>
    /// 所属客户端的控制面协议版本，决定下发 SwapMsg 时用 JSON 还是旧版竖线文本。
    /// </summary>
    public int ProtocolVersion { get; set; } = ControlProtocol.LegacyVersion;
}
