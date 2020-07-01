using FastTunnel.Core;
using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Host;
using FastTunnel.Core.Logger;
using FastTunnel.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Extensions.Logging;
using SuiDao.Client.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SuiDao.Client
{
    class Program
    {
        const string KeyLogName = ".key";

        /// <summary>
        /// suidao.io 客户端
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            LogManager.Configuration = NlogConfig.getNewConfig();
            var logger = LogManager.GetCurrentClassLogger();
            logger.Info("===== SuiDao Client Start =====");

            var keyFile = Path.Combine(AppContext.BaseDirectory, KeyLogName);
            if (!File.Exists(keyFile))
            {
                NewKey(logger);
                return;
            }

            List<string> keys = new List<string>();
            using (var reader = new StreamReader(keyFile))
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        keys.Add(line);
                    }
                }
            }

            keys = keys.Distinct().ToList();
            if (keys.Count > 0)
            {
                Console.WriteLine("请选择要启动的客户端：" + Environment.NewLine);

                Console.WriteLine($" 0：其他密钥登录");
                for (int i = 0; i < keys.Count; i++)
                {
                    Console.WriteLine($" {i + 1}：{keys[i]}");
                }

                Console.WriteLine(Environment.NewLine + "输入编号回车键继续：");

                HandleNum(keys, logger);
                return;
            }
        }

        private static void NewKey(ILogger logger)
        {
            string key;
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

            LogByKey(key, logger, true);
        }

        private static void HandleNum(List<string> keys, ILogger logger)
        {
            while (true)
            {
                var str = Console.ReadLine();
                if (string.IsNullOrEmpty(str))
                {
                    continue;
                }

                int index;
                if (!int.TryParse(str, out index))
                {
                    Console.WriteLine("输入错误 请重新选择");
                    continue;
                }

                if (index < 0 || index > keys.Count)
                {
                    Console.WriteLine("输入错误 请重新选择");
                    continue;
                }

                if (index == 0)
                {
                    NewKey(logger);
                }
                else
                {
                    LogByKey(keys[index - 1], logger, false);
                }

                break;
            }
        }

        static IServiceProvider servicesProvider;

        private static void LogByKey(string key, ILogger logger, bool log)
        {
            Console.WriteLine("登录中...");

            try
            {
                if (servicesProvider == null)
                    servicesProvider = new Host().Config(Config).Build();

                Run(servicesProvider, logger, key, log);

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

        private static void Run(IServiceProvider servicesProvider, ILogger _logger, string key, bool log)
        {
            var res_str = HttpHelper.PostAsJson("https://api1.suidao.io/api/Client/GetServerByKey", $"{{ \"key\":\"{key}\"}}").Result;
            var jobj = JObject.Parse(res_str);
            if ((bool)jobj["success"] == true)
            {
                // 记录登录记录
                if (log)
                {
                    AppendTextToFile(Path.Combine(AppContext.BaseDirectory, KeyLogName), Environment.NewLine + key);
                }

                SuiDaoServerInfo server;
                var res = jobj["data"].ToObject<SuiDaoServerConfig>();
                if (res.servers != null && res.servers.Count() > 0)
                {
                    // 选择其中一个服务器继续
                    if (res.servers.Count() == 1)
                    {
                        server = res.servers.First();
                    }
                    else
                    {
                        Console.WriteLine("请选择其中一个服务器进行连接（输入序号，回车键确认）：");
                        for (int i = 0; i < res.servers.Length; i++)
                        {
                            Console.WriteLine($"{i}:{res.servers[i].server_name}");
                        }

                        while (true)
                        {
                            var input = Console.ReadLine();
                            int index;
                            if (int.TryParse(input, out index) && index <= res.servers.Length - 1 && index >= 0)
                            {
                                // 输入有效，退出循环
                                server = res.servers[index];
                                Console.WriteLine($"您选择的服务器为：{server.server_name}");
                                break;
                            }
                            else
                            {
                                Console.WriteLine("输入有误，请重新输入");
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("您无可用的服务器");
                    NewKey(_logger);
                    return;
                }

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
                    _client.Send(new Message<LogInByKeyMassage> { MessageType = MessageType.C_LogIn, Content = new LogInByKeyMassage { key = key, server_id = server.server_id } });

                    return _client;
                }, new SuiDaoServer { ServerAddr = server.ip, ServerPort = server.bind_port });
            }
            else
            {
                Console.WriteLine(jobj["errorMsg"].ToString());
                NewKey(_logger);
            }
        }

        private static void Config(ServiceCollection service)
        {
            service.AddSingleton<FastTunnelClient>()
                 .AddSingleton<ClientHeartHandler>()
                 .AddSingleton<LogHandler>()
                 .AddSingleton<NewCustomerHandler>()
                 .AddSingleton<NewSSHHandler>();
        }

        public static void AppendTextToFile(string filename, string inputStr)
        {
            var dir = Path.GetDirectoryName(filename);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (FileStream fsw = new FileStream(filename, FileMode.Append))
            {
                byte[] writeBytes = Encoding.UTF8.GetBytes(inputStr);
                fsw.Write(writeBytes, 0, writeBytes.Length);
                fsw.Close();
            }
        }
    }
}
