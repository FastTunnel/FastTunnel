// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class SocketExtensions
    {
        public static void SendCmd<T>(this Socket socket, Message<T> message)
            where T : TunnelMassage
        {
            socket.Send(Encoding.UTF8.GetBytes(message.ToJson() + "\n"));
        }
    }
}
