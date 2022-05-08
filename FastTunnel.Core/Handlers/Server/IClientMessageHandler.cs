// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Net.WebSockets;
using System.Threading.Tasks;
using FastTunnel.Core.Client;

namespace FastTunnel.Core.Handlers.Server;

public interface IClientMessageHandler
{
    bool NeedRecive { get; }

    Task<bool> HandlerMsg(FastTunnelServer server, WebSocket client, string msg);
}
