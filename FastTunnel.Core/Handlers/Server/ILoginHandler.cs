// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Threading.Tasks;
using FastTunnel.Core.Models;
using FastTunnel.Core.Server;

namespace FastTunnel.Core.Handlers.Server;

public interface ILoginHandler
{
    Task<bool> HandlerMsg(FastTunnelServer fastTunnelServer, TunnelClient tunnelClient, string lineCmd);
}
