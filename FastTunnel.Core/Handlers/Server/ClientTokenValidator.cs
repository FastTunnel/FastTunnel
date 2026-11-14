// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FastTunnel.Core.Protocol;
using Microsoft.AspNetCore.Http;

namespace FastTunnel.Core.Handlers.Server;

/// <summary>
/// 控制面 token 校验。除了通过/拒绝，还把命中的 token 返回给调用方，
/// 记入 <c>TunnelClient.Token</c> —— 后续封禁与审计要靠它把一次访问定位到具体主体。
/// </summary>
public static class ClientTokenValidator
{
    /// <param name="matched">校验通过时返回配置中命中的那一条；未启用校验时为 null。</param>
    public static bool TryValidate(HttpContext context, IReadOnlyList<string>? tokens, out string? matched)
    {
        matched = null;
        if (tokens == null || tokens.Count == 0)
        {
            // 未配置 Tokens：保持原有「不校验」行为
            return true;
        }

        if (!context.Request.Headers.TryGetValue(ProtocolConst.FASTTUNNEL_TOKEN, out var supplied))
        {
            return false;
        }

        var value = supplied.ToString();
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            // 定长比较，避免按字符短路造成的时序泄漏
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(value)))
            {
                matched = token;
                return true;
            }
        }

        return false;
    }
}
