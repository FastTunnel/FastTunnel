// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace FastTunnel.Server
{
    public class FastTunnelConnectionMiddleware
    {
        private ConnectionDelegate next;
        private int index;

        public FastTunnelConnectionMiddleware(ConnectionDelegate next, int index)
        {
            this.next = next;
            this.index = index;
        }

        PipeReader _input;
        internal async Task OnConnectionAsync(ConnectionContext context)
        {
            var oldTransport = context.Transport;
            _input = oldTransport.Input;

            await ReadPipeAsync(_input);

            try
            {
                await next(context);
            }
            finally
            {
                context.Transport = oldTransport;
            }
        }

        async Task ReadPipeAsync(PipeReader reader)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition? position = null;

                do
                {
                    // 在缓冲数据中查找找一个行末尾
                    position = buffer.PositionOf((byte)'\r\n');

                    if (position != null)
                    {
                        // 处理这一行
                        ProcessLine(buffer.Slice(0, position.Value));

                        // 跳过 这一行+\n (basically position 主要位置？)
                        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                    }
                }
                while (position != null);

                // 告诉PipeReader我们以及处理多少缓冲
                reader.AdvanceTo(buffer.Start, buffer.End);

                // 如果没有更多的数据，停止都去
                if (result.IsCompleted)
                {
                    break;
                }
            }

            // 将PipeReader标记为完成
            reader.Complete();
        }

        private void ProcessLine(ReadOnlySequence<byte> readOnlySequence)
        {
            var str = Encoding.UTF8.GetString(readOnlySequence);

            Console.WriteLine($"[Handle] {str}");
        }

        public class TestDuplexPipe : IDuplexPipe, IDisposable
        {
            public TestDuplexPipe(IDuplexPipe Transport)
            {

            }

            public PipeReader Input => throw new NotImplementedException();

            public PipeWriter Output => throw new NotImplementedException();

            public void Dispose()
            {
                Input.CompleteAsync();
                Output.CompleteAsync();
            }
        }
    }
}
