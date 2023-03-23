// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FastTunnel.Api.Filters;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Server.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

namespace FastTunnel.Server;

public class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console().WriteTo.File("Logs/log-.log", rollingInterval: RollingInterval.Day)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args
            });

            // Add services to the container.
            builder.Services.AddSingleton<CustomExceptionFilterAttribute>();
            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
#if DEBUG
            builder.Services.AddSwaggerGen();
#endif
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("corsPolicy", policy =>
                {
                    policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()
                        .WithExposedHeaders("Set-Token");
                });
            });

            builder.Host.UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console());

            (builder.Configuration as IConfigurationBuilder).AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true);
            (builder.Configuration as IConfigurationBuilder).AddJsonFile($"config/appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true); ;

            // -------------------FastTunnel STEP1 OF 3------------------
            builder.Services.AddFastTunnelServer(builder.Configuration.GetSection("FastTunnel"));
            // -------------------FastTunnel STEP1 END-------------------

            var Configuration = builder.Configuration;
            var apioptions = Configuration.GetSection("FastTunnel").Get<DefaultServerConfig>();

            builder.Services.AddAuthentication("Bearer").AddJwtBearer(delegate (JwtBearerOptions options)
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(apioptions.Api.JWT.ClockSkew),
                    ValidateIssuerSigningKey = true,
                    ValidAudience = apioptions.Api.JWT.ValidAudience,
                    ValidIssuer = apioptions.Api.JWT.ValidIssuer,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apioptions.Api.JWT.IssuerSigningKey))
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async delegate (JwtBearerChallengeContext context)
                    {
                        context.HandleResponse();
                        context.Response.ContentType = "application/json;charset=utf-8";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            code = -1,
                            message = context.Error ?? "未登录"
                        });
                    }
                };
            });

            builder.Host.UseWindowsService();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
#if DEBUG
                app.UseSwagger();
                app.UseSwaggerUI();
#endif
            }

            app.UseCors("corsPolicy");
            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAuthentication();

            app.UseAuthorization();

            app.MapControllers();

            // -------------------FastTunnel STEP2 OF 3------------------
            app.UseFastTunnelServer();
            // -------------------FastTunnel STEP2 END-------------------

            app.MapFastTunnelServer();

            app.Run();
        }
        catch (System.Exception ex)
        {
            Log.Fatal(ex, "致命异常");
            throw;
        }
    }
}
