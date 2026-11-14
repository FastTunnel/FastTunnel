// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace FastTunnel.Core.Tests;

/// <summary>token 校验要把命中的凭据交给会话，否则封禁无法定位到主体。</summary>
public class ClientTokenValidatorTests
{
    private static DefaultHttpContext Context(StringValues? token)
    {
        var context = new DefaultHttpContext();
        if (token.HasValue)
        {
            context.Request.Headers[ProtocolConst.FASTTUNNEL_TOKEN] = token.Value;
        }

        return context;
    }

    [Fact]
    public void 未配置Tokens时保持原有的不校验行为()
    {
        Assert.True(ClientTokenValidator.TryValidate(Context(null), null, out var matched));
        Assert.Null(matched);

        Assert.True(ClientTokenValidator.TryValidate(Context(null), Array.Empty<string>(), out matched));
        Assert.Null(matched);
    }

    [Fact]
    public void 缺少Token头时拒绝()
    {
        Assert.False(ClientTokenValidator.TryValidate(Context(null), new[] { "secret" }, out var matched));
        Assert.Null(matched);
    }

    [Fact]
    public void 错误的Token被拒绝()
    {
        Assert.False(ClientTokenValidator.TryValidate(Context("wrong"), new[] { "secret" }, out _));
    }

    [Fact]
    public void 校验通过时返回命中的那一条Token()
    {
        var tokens = new List<string> { "first", "secret", "third" };

        Assert.True(ClientTokenValidator.TryValidate(Context("secret"), tokens, out var matched));
        Assert.Equal("secret", matched);
    }

    [Fact]
    public void 多值Token头不能蒙混过关()
    {
        var multi = new StringValues(new[] { "secret", "extra" });

        Assert.False(ClientTokenValidator.TryValidate(Context(multi), new[] { "secret" }, out _));
    }

    [Fact]
    public void 空白Token与空白配置项都无效()
    {
        Assert.False(ClientTokenValidator.TryValidate(Context(""), new[] { "secret" }, out _));
        Assert.False(ClientTokenValidator.TryValidate(Context(""), new[] { "", " " }, out _));
    }
}
