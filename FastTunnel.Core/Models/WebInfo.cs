// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.WebSockets;

namespace FastTunnel.Core.Models;

public class WebInfo
{
    public WebSocket Socket { get; set; }

    public WebConfig WebConfig { get; set; }

    internal void LogOut()
    {
        // TODO:退出登录
    }
}
