// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Security;

namespace FastTunnel.Core.Protocol;

/// <summary>
/// 使用默认命名策略（PascalCase）的类型的 NativeAOT 序列化元数据。
/// 覆盖三条彼此独立、原先各自 new JsonSerializerOptions 的路径：
/// 客户端登录消息（ObjectExtensions.ToJson）、服务端 LogIn 反序列化（LoginHandler）、
/// 审计日志（AuditLog）与黑名单状态文件（BanRegistry）。
/// 这些类型的 wire / 文件格式原本就不是 camelCase，保持不变。
/// </summary>
[JsonSerializable(typeof(LogInMassage))]
[JsonSerializable(typeof(WebConfig))]
[JsonSerializable(typeof(ForwardConfig))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(BanRecord))]
[JsonSerializable(typeof(PeerRegistryFile))]
[JsonSerializable(typeof(PeerRecord))]
[JsonSerializable(typeof(EnrollTicket))]
[JsonSerializable(typeof(PeerNetworkRoster))]
[JsonSerializable(typeof(PeerNetworkEntry))]
[JsonSerializable(typeof(PeerNetworkEntry[]))]
[JsonSerializable(typeof(List<PeerRecord>))]
[JsonSerializable(typeof(List<EnrollTicket>))]
[JsonSerializable(typeof(List<BanRecord>))]
[JsonSerializable(typeof(IReadOnlyList<BanRecord>))]
[JsonSerializable(typeof(BanRecord[]))]
[JsonSerializable(typeof(JwtChallengeResponse))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

/// <summary>
/// JWT 认证失败时的响应体。原先是匿名对象 new { errorCode = 1, errorMessage = ... }，
/// 匿名类型无法在 NativeAOT 下登记元数据，改为具名类型以保持 wire 格式不变。
/// </summary>
public sealed class JwtChallengeResponse
{
    public int errorCode { get; set; }

    public string errorMessage { get; set; } = string.Empty;
}

/// <summary>NativeAOT 安全的 JSON 辅助，供其他程序集序列化已登记的具名类型。</summary>
public static class AotJson
{
    public static string SerializeJwtChallenge(JwtChallengeResponse response)
        => System.Text.Json.JsonSerializer.Serialize(response, AppJsonContext.Default.JwtChallengeResponse);
}
