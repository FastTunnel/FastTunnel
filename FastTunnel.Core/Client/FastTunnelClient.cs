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
using FastTunnel.Core.Protocol;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient : IFastTunnelClient
    {
        //Socket _client;
        private IFastTunnelClientSocket socket;

        protected ILogger<FastTunnelClient> _logger;

        public DateTime lastHeart;

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
                return;
            }

            await ReceiveServerAsync(socket);
        }

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
                    Forwards = _configuration.CurrentValue.Forwards,
                },
            };

            // 登录
            await socket.SendAsync(loginMsg, cancellationToken);
            return socket;
        }

        private async Task ReceiveServerAsync(IFastTunnelClientSocket client)
        {
            byte[] buffer = new byte[512];
            var tunnelProtocol = new TunnelProtocol();

            while (true)
            {
                var res = await client.ReceiveAsync(buffer, CancellationToken.None);
                var cmds = tunnelProtocol.HandleBuffer(buffer, 0, res);

                if (cmds == null)
                    continue;

                foreach (var item in cmds)
                {
                    HandleServerRequestAsync(item);
                }
            }
        }

        private async void HandleServerRequestAsync(string lineCmd)
        {
            try
            {
                var cmds = lineCmd.Split("||");
                var type = cmds[0];

                _logger.LogInformation($"处理 {type}");
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

                await handler.HandlerMsgAsync(this, msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }
    }
}
