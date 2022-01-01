// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class ByteArrayExtensions
    {
        public static string GetString(this byte[] buffer, int offset, int count)
        {
            return Encoding.UTF8.GetString(buffer, offset, count);
        }
    }
}
