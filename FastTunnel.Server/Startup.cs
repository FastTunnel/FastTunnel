// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using System;
using FastTunnel.Core.Config;
using System.Text;

#if DEBUG
using Microsoft.OpenApi.Models;
#endif

namespace FastTunnel.Server;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var serverOptions = Configuration.GetSection("FastTunnel").Get<DefaultServerConfig>();

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

        services.AddAuthorization();

        services.AddControllers();

#if DEBUG
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v2", new OpenApiInfo { Title = "FastTunel.Api", Version = "v2" });
        });
#endif

        // -------------------FastTunnel STEP1 OF 3------------------
        services.AddFastTunnelServer(Configuration.GetSection("FastTunnel"));
        // -------------------FastTunnel STEP1 END-------------------
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
#if DEBUG
            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v2/swagger.json", "FastTunel.WebApi v2"));
#endif
        }

        // -------------------FastTunnel STEP2 OF 3------------------
        app.UseFastTunnelServer();
        // -------------------FastTunnel STEP2 END-------------------

        app.UseRouting();

        // --------------------- Custom UI ----------------
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        // --------------------- Custom UI ----------------

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            // -------------------FastTunnel STEP3 OF 3------------------
            endpoints.MapFastTunnelServer();
            // -------------------FastTunnel STEP3 END-------------------
        });
    }
}
