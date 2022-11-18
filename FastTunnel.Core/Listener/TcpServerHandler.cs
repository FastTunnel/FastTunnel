// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeetleX;
using BeetleX.EventArgs;

namespace FastTunnel.Core.Listener;

internal class TcpServerHandler : ServerHandlerBase
{
    PortProxyListenerV2 proxyListenerV2;
         
    public override void Connected(IServer server, ConnectedEventArgs e)
    {
        Console.WriteLine("[Connected]");
    }

    public override void Connecting(IServer server, ConnectingEventArgs e)
    {
        Console.WriteLine("[Connecting]");
    }

    public override void Disconnect(IServer server, SessionEventArgs e)
    {
        Console.WriteLine("[Disconnect]");
    }

    public override void Error(IServer server, ServerErrorEventArgs e)
    {
        Console.WriteLine("[Error]");
    }

    public override void Opened(IServer server)
    {
        Console.WriteLine("[Opened]");
    }

    public override void SessionDetection(IServer server, SessionDetectionEventArgs e)
    {
        Console.WriteLine("[SessionDetection]");
    }

    public override void SessionPacketDecodeCompleted(IServer server, PacketDecodeCompletedEventArgs e)
    {
        Console.WriteLine("[SessionPacketDecodeCompleted]");
    }

    public override void SessionReceive(IServer server, SessionReceiveEventArgs e)
    {
        Console.WriteLine("[SessionReceive]");

        proxyListenerV2.Process(e);
    }

    internal void Sethanler(PortProxyListenerV2 portProxyListenerV2)
    {
        this.proxyListenerV2 = portProxyListenerV2;
    }
}
