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
using FastTunnel.Core.Utilitys;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient : IFastTunnelClient
    {
        private ClientWebSocket socket;

        protected readonly ILogger<FastTunnelClient> _logger;
        private readonly SwapHandler _newCustomerHandler;
        private readonly LogHandler _logHandler;

        public DefaultClientConfig ClientConfig { get; private set; }

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
            Server = ClientConfig.Server;
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

        private async Task loginAsync(CancellationToken cancellationToken)
        {
            try
            {
                var logMsg = GetLoginMsg(cancellationToken);

                // 连接到的目标IP
                socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = delegate { return true; };
                socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_VERSION, AssemblyUtility.GetVersion().ToString());
                socket.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_TOKEN, ClientConfig.Token);

                _logger.LogInformation($"正在连接服务端 {Server.ServerAddr}:{Server.ServerPort}");
                await socket.ConnectAsync(
                    new Uri($"ws://{Server.ServerAddr}:{Server.ServerPort}"), cancellationToken);

                _logger.LogDebug("连接服务端成功");

                // 登录
                await socket.SendCmdAsync(MessageType.LogIn, logMsg, cancellationToken);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public virtual string GetLoginMsg(CancellationToken cancellationToken)
        {
            Server = ClientConfig.Server;
            return new LogInMassage
            {
                Webs = ClientConfig.Webs,
                Forwards = ClientConfig.Forwards,
            }.ToJson();
        }

        private async Task ReceiveServerAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[FastTunnelConst.MAX_CMD_LENGTH];
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
            await Task.Yield();

            try
            {
                IClientHandler handler;
                switch ((MessageType)cmd)
                {
                    case MessageType.SwapMsg:
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

        public void Stop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Stoping =====");
            if (socket == null)
                return;

            if (socket.State == WebSocketState.Connecting)
                return;

            socket.CloseAsync(WebSocketCloseStatus.Empty, string.Empty, cancellationToken);
        }
    }
}
