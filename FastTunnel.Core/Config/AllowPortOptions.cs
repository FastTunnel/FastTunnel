// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

using System;

namespace FastTunnel.Core.Config;

/// <summary>
/// 服务端端口白名单条目。<see cref="Single"/> 与 <see cref="Start"/>/<see cref="End"/>
/// 二选一：单值端口，或闭区间范围。两者都填时按两条规则分别生效。
/// </summary>
public sealed class AllowPortOptions
{
    /// <summary>放行单个端口；0 表示不使用单值规则。</summary>
    public int Single { get; set; }

    /// <summary>范围起始（含）；0 表示不使用范围规则。</summary>
    public int Start { get; set; }

    /// <summary>范围结束（含）；与 Start 同时填写才生效。</summary>
    public int End { get; set; }
}
