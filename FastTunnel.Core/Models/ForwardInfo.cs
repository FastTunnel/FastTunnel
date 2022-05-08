// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.WebSockets;
using FastTunnel.Core.Listener;

namespace FastTunnel.Core.Models;

public class ForwardInfo<T>
{
    public WebSocket Socket { get; set; }

    public ForwardConfig SSHConfig { get; set; }

    public PortProxyListener Listener { get; set; }
}
