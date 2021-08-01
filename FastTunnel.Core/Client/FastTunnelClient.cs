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
using System.Net.WebSockets;
using System.Text.Json;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient : IFastTunnelClient
    {
        //Socket _client;
        private IFastTunnelClientSocket socket;

        protected ILogger<FastTunnelClient> _logger;

        System.Timers.Timer timer_heart;

        double heartInterval = 10 * 1000; // 10 秒心跳
        public DateTime lastHeart;

        int reTrySpan = 10 * 1000; // 登陆失败后重试间隔
        HttpRequestHandler _newCustomerHandler;
        NewForwardHandler _newSSHHandler;
        LogHandler _logHandler;
        ClientHeartHandler _clientHeartHandler;
        Message<LogInMassage> loginMsg;
        protected readonly IOptionsMonitor<DefaultClientConfig> _configuration;
        private readonly CancellationTokenSource cancellationTokenSource = new();

        public SuiDaoServer Server { get; protected set; }

        public FastTunnelClient(
            ILogger<FastTunnelClient> logger,
            HttpRequestHandler newCustomerHandler,
            NewForwardHandler newSSHHandler, LogHandler logHandler,
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

        private async Task reConnAsync()
        {
            Close();

            do
            {
                try
                {
                    Thread.Sleep(reTrySpan);

                    _logger.LogInformation("登录重试...");
                    socket = await loginAsync(CancellationToken.None);

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
                socket.SendAsync(new Message<HeartMassage> { MessageType = MessageType.Heart, Content = null }, cancellationTokenSource.Token).Wait();
            }
            catch (Exception)
            {
                // 与服务端断开连接
                reConnAsync();
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
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.cancellationTokenSource.Token);

            _logger.LogInformation("===== FastTunnel Client Start =====");

            try
            {
                socket = await loginAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                reConnAsync();
                return;
            }

            _ = connSuccessAsync();
        }
        //protected virtual Socket login()
        //{
        //    Server = _configuration.CurrentValue.Server;

        //    DnsSocket _client = null;
        //    _logger.LogInformation($"正在连接服务端 {Server.ServerAddr}:{Server.ServerPort}");

        //    try
        //    {
        //        // 连接到的目标IP
        //        if (_client == null)
        //        {
        //            _client = new DnsSocket(Server.ServerAddr, Server.ServerPort);
        //        }

        //        _client.Connect();

        //        _logger.LogInformation("连接成功");
        //    }
        //    catch (Exception)
        //    {
        //        throw;
        //    }

        //    loginMsg = new Message<LogInMassage>
        //    {
        //        MessageType = MessageType.C_LogIn,
        //        Content = new LogInMassage
        //        {
        //            Webs = _configuration.CurrentValue.Webs,
        //            SSH = _configuration.CurrentValue.SSH,
        //        },
        //    };

        //    // 登录
        //    _client.Send(loginMsg);

        //    return _client.Socket;
        //}
        protected virtual async Task<IFastTunnelClientSocket> loginAsync(CancellationToken cancellationToken)
        {
            Server = _configuration.CurrentValue.Server;
            _logger.LogInformation($"正在连接服务端 {Server.ServerAddr}:{Server.ServerPort}");

            try
            {
                // 连接到的目标IP
                socket = new DefultClientSocket();

                await socket.ConnectAsync(
                    new Uri($"ws://{_configuration.CurrentValue.Server.ServerAddr}:{_configuration.CurrentValue.Server.ServerPort}"), cancellationToken);

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
                    SSH = _configuration.CurrentValue.Forwards,
                },
            };

            // 登录
            await socket.SendAsync(loginMsg, cancellationToken);
            return socket;
        }

        void Close()
        {
            timer_heart.Stop();
            socket.CloseAsync();
        }

        private async Task connSuccessAsync()
        {
            _logger.LogDebug("通信已建立");

            // 心跳开始
            timer_heart.Start();

            var th = new Thread(ReceiveServer);
            th.Start(socket);
            // await new PipeHepler(_client, ProceccLine).ProcessLinesAsync();
        }

        private bool ProceccLine(Socket socket, byte[] line)
        {
            var cmd = Encoding.UTF8.GetString(line);
            HandleServerRequest(cmd);
            return true;
        }

        private void ReceiveServer(object obj)
        {
            var client = obj as IFastTunnelClientSocket;
            byte[] buffer = new byte[1024];

            string lastBuffer = string.Empty;
            int n = 0;

            while (true)
            {
                try
                {
                    n = client.ReceiveAsync(buffer, cancellationTokenSource.Token).GetAwaiter().GetResult();
                    if (n == 0)
                    {
                        client.CloseAsync();
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
                    for (int i = 0; i < msgs.Length - 1; i++)
                    {
                        var item = msgs[i];
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

                    if (string.IsNullOrEmpty(msgs[msgs.Length - 1]))
                    {
                        continue;
                    }

                    lastBuffer = msgs[msgs.Length - 1];
                    _logger.LogDebug($"lastBuffer={lastBuffer}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"HandleMsg Error {msgs.ToJson()}");
                    continue;
                }
            }

            _logger.LogInformation("stop receive from server");
        }

        private void HandleServerRequest(string lineCmd)
        {
            Task.Run(() =>
            {
                var cmds = lineCmd.Split("||");
                var type = cmds[0];

                TunnelMassage msg = null;
                IClientHandler handler;
                switch (type)
                {
                    case "Heart":
                        handler = _clientHeartHandler;
                        msg = JsonSerializer.Deserialize<HeartMassage>(cmds[1]);
                        break;
                    case "S_NewCustomer":
                        handler = _newCustomerHandler;
                        msg = JsonSerializer.Deserialize<NewCustomerMassage>(cmds[1]);
                        break;
                    case "S_NewSSH":
                        handler = _newSSHHandler;
                        msg = JsonSerializer.Deserialize<NewForwardMessage>(cmds[1]);
                        break;
                    case "Log":
                        handler = _logHandler;
                        msg = JsonSerializer.Deserialize<LogMassage>(cmds[1]);
                        break;
                    default:
                        throw new Exception($"未处理的消息：{lineCmd}");
                }

                handler.HandlerMsgAsync(this, msg);
            });
        }
    }
}
