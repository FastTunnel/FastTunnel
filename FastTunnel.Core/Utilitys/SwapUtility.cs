// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Utilitys;

internal class SwapUtility
{
    IDuplexPipe pipe1;
    IDuplexPipe pipe2;

    public SwapUtility(IDuplexPipe pipe1, IDuplexPipe pipe2)
    {
        this.pipe1 = pipe1;
        this.pipe2 = pipe2;
    }

    internal async Task SwapAsync(CancellationToken cancellationToken = default)
    {

    }

    private async Task T1(IDuplexPipe pipe, IDuplexPipe pipe1, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result;
            ReadOnlySequence<byte> readableBuffer;

            result = await pipe.Input.ReadAsync(cancellationToken);
            readableBuffer = result.Buffer;

        }
    }
}
