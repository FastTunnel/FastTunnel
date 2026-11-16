// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Text;
using FastTunnel.Api.Filters;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Protocol;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;

namespace FastTunnel.Server;

/// <summary>
/// 把原先由 IHostingStartup 按程序集名字符串动态加载的服务注册改为显式调用。
/// NativeAOT 不支持 HostingStartupAssemblies 这种运行时反射加载机制——动态加载的服务
/// （AddControllers / AddFastTunnelServer / JWT）在 AOT 下不会执行，导致 UseRouting 找不到
/// 服务、隧道核心 HostedService 也不会启动。这里显式注册让所有依赖在编译期静态可见。
///
/// 与保留的 IHostingStartup 并存是幂等的：AddControllers / AddAuthorization 重复调用无副作用，
/// Kestrel 监听配置也是叠加但相同。
/// </summary>
public static class AotServiceRegistration
{
    public static IWebHostBuilder UseFastTunnelAotServices(this IWebHostBuilder builder)
    {
        // --- 原 FastTunnelHostingStartup.Configure ---
        builder.ConfigureServices((ctx, services) =>
        {
            var section = ctx.Configuration.GetSection("FastTunnel");
            services.AddFastTunnelServer(section);
            ConfigureApiServices(ctx.Configuration, services);
        });

        builder.ConfigureKestrel((ctx, options) =>
        {
            var basePort = ctx.Configuration.GetValue<int?>("FastTunnel:BinPort") ?? 1270;
            options.ListenAnyIP(basePort, listen => listen.UseConnectionFastTunnel());
        });

        return builder;
    }

    /// <summary>原 FastTunnelApiHostingStartup.Configure 中的服务注册部分。</summary>
    private static void ConfigureApiServices(Microsoft.Extensions.Configuration.IConfiguration configuration, IServiceCollection services)
    {
        services.AddControllers();
        services.AddAuthorization();

        var serverOptions = configuration.GetSection("FastTunnel").Get<DefaultServerConfig>();
        if (serverOptions?.Api?.JWT != null)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(serverOptions.Api.JWT.ClockSkew),
                        ValidateIssuerSigningKey = true,
                        ValidAudience = serverOptions.Api.JWT.ValidAudience,
                        ValidIssuer = serverOptions.Api.JWT.ValidIssuer,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(serverOptions.Api.JWT.IssuerSigningKey)),
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnChallenge = challenge =>
                        {
                            challenge.HandleResponse();
                            challenge.Response.ContentType = "application/json;charset=utf-8";
                            challenge.Response.StatusCode = StatusCodes.Status200OK;
                            var body = new JwtChallengeResponse
                            {
                                errorCode = 1,
                                errorMessage = challenge.Error ?? "Token is Required",
                            };
                            return challenge.Response.WriteAsync(AotJson.SerializeJwtChallenge(body));
                        },
                    };
                });
        }

        services.AddSingleton<CustomExceptionFilterAttribute>();
    }
}
