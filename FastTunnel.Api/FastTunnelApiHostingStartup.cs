// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Api;
using FastTunnel.Api.Filters;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

[assembly: HostingStartup(typeof(FastTunnelApiHostingStartup))]

namespace FastTunnel.Api;

public class FastTunnelApiHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        Debug.WriteLine("FastTunnelApiHostingStartup Configured");

        builder.ConfigureServices((webHostBuilderContext, services) =>
        {
            services.AddControllers();

            var serverOptions = webHostBuilderContext.Configuration.GetSection("FastTunnel").Get<DefaultServerConfig>();
            if (serverOptions.Api?.JWT != null)
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
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(serverOptions.Api.JWT.IssuerSigningKey))
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnChallenge = async context =>
                        {
                            context.HandleResponse();

                            context.Response.ContentType = "application/json;charset=utf-8";
                            context.Response.StatusCode = StatusCodes.Status200OK;

                            await context.Response.WriteAsync(new
                            {
                                errorCode = 1,
                                errorMessage = context.Error ?? "Token is Required"
                            }.ToJson());
                        },
                    };
                });
            }

            services.AddSingleton<CustomExceptionFilterAttribute>();
        });
    }
}
