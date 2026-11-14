// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

namespace FastTunnel.Core.Models;

public class WebConfig
{
    /// <summary>
    /// 子域名
    /// </summary>
    public string SubDomain { get; set; }

    /// <summary>
    /// 本地IP
    /// </summary>
    public string LocalIp { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public int LocalPort { get; set; }

    /// <summary>
    /// 个人域名
    /// </summary>
    public string[] WWW { get; set; }

    /// <summary>
    /// 负载均衡组名。多个客户端用相同的组名 + 组口令 + 相同域名注册时，
    /// 服务端把它们聚合为一个组，按请求随机分发给组内存活成员（对齐 frp 的
    /// loadBalancer.group 语义）。不填则保持旧版行为：同域名后来者被拒绝或顶替。
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// 负载均衡组口令。同组成员必须一致，防止不相干客户端混入组劫持流量。
    /// 仅在 <see cref="Group"/> 非空时生效。
    /// </summary>
    public string? GroupKey { get; set; }
}
