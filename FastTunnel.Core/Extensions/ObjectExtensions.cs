// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H
using System.Text.Json;

namespace FastTunnel.Core.Extensions
{
    public static class ObjectExtensions
    {
        public static string ToJson(this object message)
        {
            if (message == null)
            {
                return null;
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            return JsonSerializer.Serialize(message, message.GetType(), jsonOptions);
        }
    }
}
