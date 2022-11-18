// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H
#if NETCOREAPP3_1_OR_GREATER
using System.Text.Json;
#else

#endif

namespace FastTunnel.Core.Extensions
{
    public static class ObjectExtensions
    {
#if NETCOREAPP3_1_OR_GREATER
        public static string ToJson(this object message)
        {
            if (message == null)
            {
                return null;
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            return JsonSerializer.Serialize(message, message.GetType(), jsonOptions);
        }
#else
        public static string ToJson(this object message)
        {
            if (message == null)
            {
                return null;
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            return JsonSerializer.Serialize(message, message.GetType(), jsonOptions);
        }
#endif
    }
}
