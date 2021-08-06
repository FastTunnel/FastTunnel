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

        SwapHandler _newCustomerHandler;
        LogHandler _logHandler;

        public DefaultClientConfig ClientConfig { get; private set; }
        private readonly CancellationTokenSource cancellationTokenSource = new();

        public SuiDaoServer Server { get; protected set; }

        public FastTunnelClient(
            ILogger<FastTunnelClient> logger,
            SwapHandler newCustomerHandler,
             LogHandler logHandler,
            IOptionsMonitor<DefaultClientConfig> configuration)
        {
            _logger = logger;
            _newCustomerHandler = newCustomerHandler;
            _logHandler = logHandler;
            ClientConfig = configuration.CurrentValue;
        }

        /// <summary>
        /// 启动客户端
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="customLoginMsg">自定义登录信息，可进行扩展业务</param>
        public async void StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Start =====");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await loginAsync(cancellationToken);
                    await ReceiveServerAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }

            _logger.LogInformation("===== FastTunnel Client End =====");
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
                socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_FLAG, "2.0.0");
                socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_TYPE, FastTunnelConst.TYPE_CLIENT);

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
            }.ToJson(), cancellationToken);
        }

        private async Task ReceiveServerAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[128];

            while (!cancellationToken.IsCancellationRequested)
            {
                var res = await socket.ReceiveAsync(buffer, cancellationToken);
                var type = buffer[0];
                var content = Encoding.UTF8.GetString(buffer, 1, res.Count - 1);
                HandleServerRequestAsync(type, content, cancellationToken);
            }
        }

        private async void HandleServerRequestAsync(byte cmd, string ctx, CancellationToken cancellationToken)
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

                await handler.HandlerMsgAsync(this, ctx, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }
    }
}
