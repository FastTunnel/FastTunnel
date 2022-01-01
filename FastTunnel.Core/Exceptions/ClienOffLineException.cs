// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;

namespace FastTunnel.Core.Exceptions
{
    public class ClienOffLineException : Exception
    {
        public ClienOffLineException(string message)
            : base(message)
        {
        }
    }
}
