using FastTunnel.Core;
using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Host;
using FastTunnel.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using SuiDao.Client.Models;
using System;
using System.Net.Sockets;
using System.Threading;

namespace SuiDao.Client
{
    class Program
    {
        /// <summary>
        /// suidao.io 客户端
        /// </summary>
        /// <param name="args"></param>
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
            var res = HttpHelper.PostAsJson("https://api1.suidao.io/api/Client/GetServerByKey", $"{{ \"key\":\"{key}\"}}").Result;
            var jobj = JObject.Parse(res);
            if ((bool)jobj["success"] == true)
            {
                var server = jobj["data"].ToObject<SuiDaoServerConfig>();

                var client = servicesProvider.GetRequiredService<FastTunnelClient>();

                client.Login(() =>
                {

                    Connecter _client = null;

                    try
                    {
                        _client = new Connecter(server.ip, server.bind_port);

                        _client.Connect();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex.Message);
                        _client.Socket.Close();
                        throw;
                    }

                    // 登录
                    _client.Send(new Message<LogInByKeyRequest> { MessageType = MessageType.C_LogIn, Content = new LogInByKeyRequest { key = key } });

                    return _client;
                }, new SuiDaoServer { ServerAddr = server.ip, ServerPort = server.bind_port });


            }
            else
            {
                Console.WriteLine(jobj["errorMsg"].ToString());
            }
        }

        private static void Config(ServiceCollection service)
        {
            service.AddTransient<FastTunnelClient>();
        }
    }
}
