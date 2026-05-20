// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Extensions;
using System.Text;

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
        services.AddOpenApi();
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseRouting();

        // --------------------- Custom UI ----------------
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        // --------------------- Custom UI ----------------
     
        app.UseFastTunnelServer();

        app.UseEndpoints(endpoints =>
        {
            if (env.IsDevelopment())
            {
                endpoints.MapOpenApi();
            }
           
            endpoints.MapControllers();
            endpoints.MapFallback(async (HttpContext ctx) =>
            {
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("404~"));
            });
        });
    }
}
