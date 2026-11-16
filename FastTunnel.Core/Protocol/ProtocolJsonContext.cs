// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTunnel.Core.Protocol;

/// <summary>
/// 控制面消息的 NativeAOT 序列化元数据。
/// 选项与旧的 <c>ControlProtocol.JsonOptions</c>（JsonSerializerDefaults.Web +
/// WhenWritingNull）保持一致，因此 wire 格式不变：camelCase、大小写不敏感、
/// 数字可从字符串读取。运行时不要 new 自己的 JsonSerializerOptions 再挂这个 context，
/// 必须直接使用 <see cref="Default"/> 的 Options（源生成的元数据与它的 Options 绑定）。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SwapRequest))]
[JsonSerializable(typeof(LogMessage))]
[JsonSerializable(typeof(HeartbeatMessage))]
[JsonSerializable(typeof(List<SwapRequest>))]
internal partial class ProtocolJsonContext : JsonSerializerContext
{
}
