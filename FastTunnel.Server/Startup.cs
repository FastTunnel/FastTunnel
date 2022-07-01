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
using FastTunnel.Api.Filters;

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

        app.UseRouting();

        // -------------------FastTunnel STEP2 OF 3------------------
        app.UseFastTunnelServer();
        // -------------------FastTunnel STEP2 END-------------------

        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            // -------------------FastTunnel STEP3 OF 3------------------
            endpoints.MapFastTunnelServer();
            // -------------------FastTunnel STEP3 END-------------------
        });
    }
}
