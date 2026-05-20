// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Handlers;

namespace FastTunnel.Core.Listener
{
    /// <summary>
    /// Public-side port listener (TCP or UDP) used by the FastTunnel server
    /// to accept user traffic and dispatch it through a tunnel.
    /// </summary>
    public interface IPortListener
    {
        string ListenIp { get; set; }

        int ListenPort { get; set; }

        void Start(ForwardDispatcher requestDispatcher);

        void Stop();
    }
}
