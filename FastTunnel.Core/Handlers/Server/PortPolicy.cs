// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using FastTunnel.Core.Config;

namespace FastTunnel.Core.Handlers.Server;

/// <summary>
/// TCP/UDP 转发的服务端端口准入：
/// <list type="bullet">
/// <item>端口合法性（1–65535）；</item>
/// <item><see cref="DefaultServerConfig.AllowPorts"/> 白名单——配置了条目时，未命中的端口一律拒绝，
///     防止客户端把恶意服务挂在 22/25/445 等知名端口，或抢占服务端自有监听；</item>
/// </list>
/// 与域名侧的 <see cref="HostNameValidator"/> 黑名单配对：HTTP 靠域名收口，TCP/UDP 靠端口收口。
/// 每次登录都从 <c>ServerOption.CurrentValue</c> 现读配置，天然跟随热加载。
/// </summary>
public static class PortPolicy
{
    public static bool IsPortAllowed(IReadOnlyList<AllowPortOptions>? allowPorts, int remotePort, out string reason)
    {
        if (remotePort is < 1 or > 65535)
        {
            reason = "端口非法（须为 1–65535）";
            return false;
        }

        if (allowPorts == null || allowPorts.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        var hasValidEntry = false;
        foreach (var entry in allowPorts)
        {
            if (entry == null)
            {
                continue;
            }

            if (entry.Single > 0)
            {
                hasValidEntry = true;
                if (remotePort == entry.Single)
                {
                    reason = string.Empty;
                    return true;
                }
            }

            if (entry.Start > 0 && entry.End >= entry.Start)
            {
                hasValidEntry = true;
                if (remotePort >= entry.Start && remotePort <= entry.End)
                {
                    reason = string.Empty;
                    return true;
                }
            }
        }

        reason = hasValidEntry
            ? "不在服务端端口白名单内"
            : "服务端端口白名单配置为空条目（Start/End 或 Single 均未正确填写）";
        return false;
    }
}
