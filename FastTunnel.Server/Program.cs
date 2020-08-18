using Microsoft.Extensions.Configuration;
using FastTunnel.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NLog.Extensions.Logging;
using FastTunnel.Core.Core;
using FastTunnel.Core.Handlers;
using FastTunnel.Core.Handlers.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using FastTunnel.Server.Service;
using FastTunnel.Core.Logger;

namespace FastTunnel.Server
{
    class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        /// <summary>
        /// 使用托管服务实现后台任务:https://docs.microsoft.com/zh-cn/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-5.0&tabs=visual-studio
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging((context) =>
                {
                    context.AddNLog(NlogConfig.getNewConfig());
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<ServiceFastTunnelServer>();

                    // DI
                    services.AddTransient<FastTunnelServer>();
                    services.AddTransient<LoginHandler>();
                    services.AddTransient<HeartHandler>();
                    services.AddTransient<SwapMsgHandler>();
                    services.AddTransient<IConfigHandler, ConfigHandler>();
                });
    }
}
