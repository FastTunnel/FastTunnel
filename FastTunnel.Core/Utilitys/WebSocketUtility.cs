// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Utilitys
{


    public class WebSocketUtility
    {
        private readonly WebSocket webSocket;

        public WebSocketUtility(WebSocket webSocket, Action<ReadOnlySequence<byte>, CancellationToken> processLine)
        {
            this.webSocket = webSocket;
            ProcessLine = processLine;
        }

        public Action<ReadOnlySequence<byte>, CancellationToken> ProcessLine { get; }

        public async Task ProcessLinesAsync(CancellationToken cancellationToken)
        {
            var pipe = new Pipe();
            var writing = FillPipeAsync(webSocket, pipe.Writer, cancellationToken);
            var reading = ReadPipeAsync(pipe.Reader, cancellationToken);

            await Task.WhenAll(reading, writing);
        }

        /// <summary>
        /// 读取socket收到的消息写入Pipe
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="writer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        private async Task FillPipeAsync(WebSocket socket, PipeWriter writer, CancellationToken cancellationToken)
        {
            const int minimumBufferSize = 512;

            while (true)
            {
                // Allocate at least 512 bytes from the PipeWriter.
                var memory = writer.GetMemory(minimumBufferSize);

                try
                {
                    var bytesRead = await socket.ReceiveAsync(memory, cancellationToken);
                    if (bytesRead.Count == 0 || bytesRead.EndOfMessage || bytesRead.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    // Tell the PipeWriter how much was read from the Socket.
                    writer.Advance(bytesRead.Count);
                }
                catch (Exception)
                {
                    break;
                }

                // Make the data available to the PipeReader.
                var result = await writer.FlushAsync(cancellationToken);

                if (result.IsCompleted)
                {
                    break;
                }
            }

            // By completing PipeWriter, tell the PipeReader that there's no more data coming.
            await writer.CompleteAsync();
        }

        /// <summary>
        /// 从Pipe中读取收到的消息
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ReadPipeAsync(PipeReader reader, CancellationToken cancellationToken)
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;

                while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                {
                    // Process the line.
                    ProcessLine(line, cancellationToken);
                }

                // Tell the PipeReader how much of the buffer has been consumed.
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming.
                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Mark the PipeReader as complete.
            await reader.CompleteAsync();
        }

        bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
        {
            // Look for a EOL in the buffer.
            SequencePosition? position = buffer.PositionOf((byte)'\n');

            if (position == null)
            {
                line = default;
                return false;
            }

            // Skip the line + the \n.
            line = buffer.Slice(0, position.Value);
            buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
            return true;
        }
    }
}
