using FastTunnel.Core;
using FastTunnel.Core.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using System.Threading;
using System.Threading.Tasks;

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
            services.AddControllers();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v2", new OpenApiInfo { Title = "FastTunel.Api", Version = "v2" });
            });

            // -------------------FastTunnel STEP1 OF 3------------------
            services.AddFastTunnelServer(Configuration.GetSection("ServerSettings"));
            // -------------------FastTunnel STEP1 END--------------------
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v2/swagger.json", "FastTunel.WebApi v2"));
            }

            // -------------------FastTunnel STEP2 OF 3------------------
            app.UseFastTunnelServer();
            // -------------------FastTunnel STEP2 END--------------------

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                // -------------------FastTunnel STEP3 OF 3------------------
                endpoints.MapFastTunnelServer();
                // -------------------FastTunnel STEP3 END--------------------
            });
        }
    }
}
