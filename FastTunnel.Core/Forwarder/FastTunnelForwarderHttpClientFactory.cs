using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Forwarder;

namespace FastTunnel.Core.Forwarder
{
    public class FastTunnelForwarderHttpClientFactory : ForwarderHttpClientFactory
    {
        ILogger<FastTunnelForwarderHttpClientFactory> _logger;
        FastTunnelServer _fastTunnelServer;

        public FastTunnelForwarderHttpClientFactory(ILogger<FastTunnelForwarderHttpClientFactory> logger, FastTunnelServer fastTunnelServer)
        {
            this._fastTunnelServer = fastTunnelServer;
            this._logger = logger;
        }

        protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
        {
            base.ConfigureHandler(context, handler);
            handler.ConnectCallback = ConnectCallback;
        }

        private async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var host = context.InitialRequestMessage.RequestUri.Host;
            _logger.LogDebug($"ConnectCallback start:{host} {context.GetHashCode()}");

            try
            {
                var res = await proxyAsync(host, cancellationToken);
                _logger.LogDebug($"ConnectCallback successfully:{host} {context.GetHashCode()}");
                return res;
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "代理出现异常");
                throw;
            }
        }

        public async ValueTask<Stream> proxyAsync(string host, CancellationToken cancellation)
        {
            WebInfo web;
            if (!_fastTunnelServer.WebList.TryGetValue(host, out web))
            {
                throw new Exception($"站点未登录:{host}");
            }

            try
            {
                var RequestId = Guid.NewGuid().ToString().Replace("-", "");
                _logger.LogInformation($"[发送swap指令]:{RequestId}");

                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{RequestId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", cancellation);

                // TODO:超时处理
                TaskCompletionSource<Stream> task = new(cancellation);
                _fastTunnelServer.ResponseTasks.TryAdd(RequestId, task);

                var res = await task.Task;
                _logger.LogInformation($"[收到swap指令]:{RequestId}");
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "proxyAsync Error");

                // 移除
                _fastTunnelServer.WebList.TryRemove(host, out _);
                throw;
            }
        }
    }
}
