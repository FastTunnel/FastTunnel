// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder
{
    public class ResponseStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        readonly MemoryStream m_Stream;

        public ResponseStream(byte[] bytes)
        {
            m_Stream = new MemoryStream(bytes);
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        bool complete;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!complete)
            {
                return 0;
            };

            var len = m_Stream.Read(buffer, offset, count);
            return len;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Console.Write(Encoding.UTF8.GetString(buffer, offset, count));
            complete = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            m_Stream.Dispose();
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            return ValueTask.CompletedTask;
        }
    }
}
