using System;
using System.Collections.Generic;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.MiddleWares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.Sample;

namespace FastTunnel.Server
{
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
            // -------------------FastTunnel START------------------
            services.AddFastTunnelServer(Configuration.GetSection("ServerSettings"));
            // -------------------FastTunnel END--------------------

            // Add the reverse proxy to capability to the server
            services.AddReverseProxy().LoadFromMemory();

            // ------------------------Custom Business------------------------------
            services.AddSingleton<IForwarderHttpClientFactory, FastTunnelForwarderHttpClientFactory>();
            services.AddSingleton<FastTunnelClientHandler, FastTunnelClientHandler>();
            services.AddSingleton<FastTunnelSwapHandler, FastTunnelSwapHandler>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
            }

            app.UseWebSockets();

            var swapHandler = app.ApplicationServices.GetRequiredService<FastTunnelSwapHandler>();
            var clientHandler = app.ApplicationServices.GetRequiredService<FastTunnelClientHandler>();
            app.Use(clientHandler.Handle);
            app.Use(swapHandler.Handle);

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapReverseProxy();
                //endpoints.MapControllers();
            });
        }
    }
}
