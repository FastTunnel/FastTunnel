using FastTunnel.Core;
using FastTunnel.Core.Client;
using FastTunnel.Core.Host;
using FastTunnel.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Net.Sockets;
using System.Threading;

namespace SuiDao.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            LogManager.LoadConfiguration("Nlog.config");
            var logger = LogManager.GetCurrentClassLogger();
            logger.Debug("===== SuiDao Client Start =====");

            string key = string.Empty;
            while (true)
            {
                Console.Write("请输入登录密钥：");
                key = Console.ReadLine();

                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                break;
            }

            Console.WriteLine("登陆中...");

            try
            {
                var servicesProvider = new Host().Config(Config).Build();
                Run(servicesProvider, logger, key);

                while (true)
                {
                    Thread.Sleep(10000 * 60);
                }
            }
            catch (Exception ex)
            {
                // NLog: catch any exception and log it.
                logger.Error(ex, "Stopped program because of exception");
                throw;
            }
            finally
            {
                // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
                LogManager.Shutdown();
            }
        }

        private static void Run(IServiceProvider servicesProvider, ILogger _logger, string key)
        {
            var client = servicesProvider.GetRequiredService<FastTunnelClient>();

            // https://localhost:5002
            //var res = HttpHelper.PostAsJson("https://api1.suidao.io/api/Client/GetServerByKey", $"{{ \"key\":\"{key}\"}}");
            var res = HttpHelper.PostAsJson("https://localhost:5002/api/Client/GetServerByKey", $"{{ \"key\":\"{key}\"}}").Result;
            var jobj = JObject.Parse(res);
            if ((bool)jobj["success"] == true)
            {
                var server = jobj["data"].ToObject<FastTunnelServer>();

            }
            else
            {

            }

            //client.Login(() =>
            //{
            //    //连接到的目标IP
            //    Connecter _client = null;

            //    try
            //    {
            //        _client = new Connecter(config.Common.ServerAddr, config.Common.ServerPort);
            //        _client.Connect();
            //    }
            //    catch (Exception ex)
            //    {
            //        _logger.Error(ex.Message);
            //        _client.Socket.Close();

            //        Thread.Sleep(5000);
            //        Login();
            //        return;
            //    }

            //    // 登录
            //    _client.Send(new Message<LogInRequest> { MessageType = MessageType.LOGIN_ERVERYONE, Content = new LogInRequest { ClientConfig = config } });

            //});
        }

        private static void Config(ServiceCollection service)
        {
            service.AddTransient<FastTunnelClient>();
        }
    }
}
