using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using System.Timers;
using System.Threading;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Handlers.Client;
using Microsoft.Extensions.Configuration;
using FastTunnel.Core.Server;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient
    {
        Socket _client;

        protected ILogger<FastTunnelClient> _logger;

        System.Timers.Timer timer_heart;

        double heartInterval = 10 * 1000; // 10 秒心跳
        public DateTime lastHeart;

        int reTrySpan = 10 * 1000; // 登陆失败后重试间隔
        HttpRequestHandler _newCustomerHandler;
        NewSSHHandler _newSSHHandler;
        LogHandler _logHandler;
        ClientHeartHandler _clientHeartHandler;
        Func<Socket> lastLogin;
        Message<LogInMassage> loginMsg;
        protected readonly IOptionsMonitor<DefaultClientConfig> _configuration;

        public SuiDaoServer Server { get; protected set; }

        public FastTunnelClient(
            ILogger<FastTunnelClient> logger,
            HttpRequestHandler newCustomerHandler,
            NewSSHHandler newSSHHandler, LogHandler logHandler,
            IOptionsMonitor<DefaultClientConfig> configuration,
            ClientHeartHandler clientHeartHandler)
        {
            _logger = logger;
            _newCustomerHandler = newCustomerHandler;
            _newSSHHandler = newSSHHandler;
            _logHandler = logHandler;
            _clientHeartHandler = clientHeartHandler;
            _configuration = configuration;

            timer_heart = new System.Timers.Timer();
            timer_heart.AutoReset = false;
            timer_heart.Interval = heartInterval;
            timer_heart.Elapsed += HeartElapsed;
        }

        private void reConn()
        {
            Close();

            do
            {
                try
                {
                    Thread.Sleep(reTrySpan);

                    _logger.LogInformation("登录重试...");
                    _client = lastLogin.Invoke();

                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            } while (true);

            connSuccessAsync();
        }

        private void HeartElapsed(object sender, ElapsedEventArgs e)
        {
            timer_heart.Enabled = false;

            try
            {
                _client.SendCmd(new Message<HeartMassage> { MessageType = MessageType.Heart, Content = null });
            }
            catch (Exception)
            {
                // 与服务端断开连接
                reConn();
            }
            finally
            {
                timer_heart.Enabled = true;
            }
        }

        /// <summary>
        /// 启动客户端
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="customLoginMsg">自定义登录信息，可进行扩展业务</param>
        public void Start()
        {
            _logger.LogInformation("===== FastTunnel Client Start =====");

            lastLogin = login;

            try
            {
                _client = lastLogin.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                reConn();
                return;
            }

            _ = connSuccessAsync();
        }

        protected virtual Socket login()
        {
            Server = _configuration.CurrentValue.Server;

            DnsSocket _client = null;
            _logger.LogInformation($"正在连接服务端 {Server.ServerAddr}:{Server.ServerPort}");

            try
            {
                // 连接到的目标IP
                if (_client == null)
                {
                    _client = new DnsSocket(Server.ServerAddr, Server.ServerPort);
                }

                _client.Connect();

                _logger.LogInformation("连接成功");
            }
            catch (Exception)
            {
                throw;
            }

            loginMsg = new Message<LogInMassage>
            {
                MessageType = MessageType.C_LogIn,
                Content = new LogInMassage
                {
                    Webs = _configuration.CurrentValue.Webs,
                    SSH = _configuration.CurrentValue.SSH,
                },
            };

            // 登录
            _client.Send(loginMsg);

            return _client.Socket;
        }

        void Close()
        {
            timer_heart.Stop();

            try
            {
                _client?.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }

            _client?.Close();
        }

        private async Task connSuccessAsync()
        {
            _logger.LogDebug("通信已建立");

            // 心跳开始
            timer_heart.Start();

            var th = new Thread(ReceiveServer);
            th.Start(_client);
            //await new PipeHepler(_client, ProceccLine).ProcessLinesAsync();
        }

        private void ReceiveServer(object obj)
        {
            var client = obj as Socket;
            byte[] buffer = new byte[1024];

            string lastBuffer = string.Empty;
            int n = 0;

            while (true)
            {
                try
                {
                    n = client.Receive(buffer);
                    if (n == 0)
                    {
                        client.Shutdown(SocketShutdown.Both);
                        break;
                    }
                }
                /// <see cref="https://docs.microsoft.com/zh-cn/windows/win32/winsock/windows-sockets-error-codes-2"/>
                catch (SocketException socketEx)
                {
                    // Connection timed out.
                    if (socketEx.ErrorCode == 10060)
                    {
                        _logger.LogInformation("Connection timed out");
                    }
                    else
                    {
                        _logger.LogError(socketEx);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                    break;
                }

                string words = Encoding.UTF8.GetString(buffer, 0, n);
                if (!string.IsNullOrEmpty(lastBuffer))
                {
                    words = lastBuffer + words;
                    lastBuffer = null;
                }

                var msgs = words.Split("\n");

                _logger.LogDebug("recive from server:" + words);

                try
                {
                    foreach (var item in msgs)
                    {
                        if (string.IsNullOrEmpty(item))
                            continue;

                        if (item.EndsWith("}"))
                        {
                            HandleServerRequest(item);
                        }
                        else
                        {
                            lastBuffer = item;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                    continue;
                }
            }

            _logger.LogInformation("stop receive from server");
        }

        private bool ProceccLine(Socket socket, byte[] line)
        {
            Task.Run(() =>
            {
                try
                {
                    var cmd = Encoding.UTF8.GetString(line);
                    HandleServerRequest(cmd);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                }
            });

            return true;
        }

        private void HandleServerRequest(string words)
        {
            var Msg = JsonConvert.DeserializeObject<Message<JObject>>(words);
            if (Msg.MessageType != MessageType.Heart)
            {
                _logger.LogDebug($"HandleServerRequest {words}");
            }

            IClientHandler handler;
            switch (Msg.MessageType)
            {
                case MessageType.Heart:
                    handler = _clientHeartHandler;
                    break;
                case MessageType.S_NewCustomer:
                    handler = _newCustomerHandler;
                    break;
                case MessageType.S_NewSSH:
                    handler = _newSSHHandler;
                    break;
                case MessageType.Log:
                    handler = _logHandler;
                    break;
                default:
                    throw new Exception($"未处理的消息：{Msg.MessageType} {Msg.Content}");
            }

            handler.HandlerMsg(this, Msg);
        }
    }
}
