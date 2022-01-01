// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public struct Message<T>
    {
        public MessageType MessageType { get; set; }

        public T Content { get; set; }
    }

    public enum MessageType : byte
    {
        LogIn = 1, // client
        SwapMsg = 2,
        Forward = 3,
        Log = 4,
    }
}
