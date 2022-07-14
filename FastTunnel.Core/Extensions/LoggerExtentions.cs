// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using Microsoft.Extensions.Logging;
using System;

namespace FastTunnel.Core.Extensions
{
    public static class LoggerExtentions
    {
        public static void LogError(this ILogger logger, Exception ex)
        {
            logger.LogError(ex, string.Empty);
        }
    }
}
