// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.Stream;

internal class SocketDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    NetworkStream stream;

    public SocketDuplexPipe(Socket socket)
    {
        stream = new NetworkStream(socket, true);

        Input = PipeReader.Create(stream);
        Output = PipeWriter.Create(stream);
    }

    public PipeReader Input { get; private set; }

    public PipeWriter Output { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
    }
}
