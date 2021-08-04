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
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text.Json;
using FastTunnel.Core.Protocol;
using Microsoft.AspNetCore.DataProtection;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient : IFastTunnelClient
    {
        private ClientWebSocket socket;
        protected ILogger<FastTunnelClient> _logger;
        public DateTime lastHeart;

        ForwardHandler _newCustomerHandler;
        LogHandler _logHandler;
        ClientHeartHandler _clientHeartHandler;
        Message<LogInMassage> loginMsg;

        public DefaultClientConfig ClientConfig { get; private set; }
        private readonly CancellationTokenSource cancellationTokenSource = new();

        public SuiDaoServer Server { get; protected set; }

        public FastTunnelClient(
            ILogger<FastTunnelClient> logger,
            ForwardHandler newCustomerHandler,
             LogHandler logHandler,
            IOptionsMonitor<DefaultClientConfig> configuration,
            ClientHeartHandler clientHeartHandler)
        {
            _logger = logger;
            _newCustomerHandler = newCustomerHandler;
            _logHandler = logHandler;
            _clientHeartHandler = clientHeartHandler;
            ClientConfig = configuration.CurrentValue;
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
            await loginAsync(cancellationToken);
            _logger.LogInformation($"通讯已建立");
            await ReceiveServerAsync();
        }

        protected virtual async Task loginAsync(CancellationToken cancellationToken)
        {
            Server = ClientConfig.Server;
            _logger.LogInformation($"正在连接服务端 {Server.ServerAddr}:{Server.ServerPort}");

            try
            {
                // 连接到的目标IP
                socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = delegate { return true; };
                socket.Options.SetRequestHeader(HeaderConst.FASTTUNNEL_FLAG, "2.0.0");
                socket.Options.SetRequestHeader(HeaderConst.FASTTUNNEL_TYPE, HeaderConst.TYPE_CLIENT);

                await socket.ConnectAsync(
                    new Uri($"ws://{ClientConfig.Server.ServerAddr}:{ClientConfig.Server.ServerPort}"), cancellationToken);

                _logger.LogInformation("连接成功");
            }
            catch (Exception)
            {
                throw;
            }

            // 登录
            await socket.SendCmdAsync(MessageType.LogIn, new LogInMassage
            {
                Webs = ClientConfig.Webs,
                Forwards = ClientConfig.Forwards,
            }.ToJson());
        }

        private async Task ReceiveServerAsync()
        {
            byte[] buffer = new byte[128];

            while (true)
            {
                var res = await socket.ReceiveAsync(buffer, CancellationToken.None);
                var type = buffer[0];
                var content = Encoding.UTF8.GetString(buffer, 1, res.Count - 1);
                HandleServerRequestAsync(type, content);
            }
        }

        private async void HandleServerRequestAsync(byte cmd, string ctx)
        {
            try
            {
                IClientHandler handler;
                switch ((MessageType)cmd)
                {
                    case MessageType.SwapMsg:
                        handler = _newCustomerHandler;
                        break;
                    case MessageType.Forward:
                        handler = _newCustomerHandler;
                        break;
                    case MessageType.Log:
                        handler = _logHandler;
                        break;
                    default:
                        throw new Exception($"未处理的消息：cmd={cmd}");
                }

                await handler.HandlerMsgAsync(this, ctx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }
    }
}
