// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

namespace FastTunnel.Core.Listener;

/// <summary>
/// 端口监听器抽象，TCP/UDP 端口转发共用
/// </summary>
public interface IPortListener
{
    /// <summary>
    /// 监听 IP
    /// </summary>
    string ListenIp { get; }

    /// <summary>
    /// 监听端口
    /// </summary>
    int ListenPort { get; }

    /// <summary>
    /// 启动监听
    /// </summary>
    void Start();

    /// <summary>
    /// 停止监听
    /// </summary>
    void Stop();
}
