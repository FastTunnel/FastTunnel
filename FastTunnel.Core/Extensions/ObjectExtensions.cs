// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Text.Json;
using FastTunnel.Core.Protocol;

namespace FastTunnel.Core.Extensions;

public static class ObjectExtensions
{
    public static string ToJson(this object message)
    {
        if (message == null)
        {
            return null;
        }

        // NativeAOT：动态类型序列化必须走源生成 context 的 resolver，
        // 且类型须在 AppJsonContext 中登记（当前唯一调用方是 LogInMassage）。
        return JsonSerializer.Serialize(message, message.GetType(), AppJsonContext.Default.Options);
    }
}
