using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Sockets;
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

            try
            {
                var res = await proxyAsync(host, context, cancellationToken);
                return res;
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "代理出现异常");
                throw;
            }
        }

        public async ValueTask<Stream> proxyAsync(string host, SocketsHttpConnectionContext context, CancellationToken cancellation)
        {
            WebInfo web;
            if (!_fastTunnelServer.WebList.TryGetValue(host, out web))
            {
                // 客户端已离线
                return await OfflinePage(host, context);
            }

            try
            {
                var RequestId = Guid.NewGuid().ToString().Replace("-", "");

                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{RequestId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", cancellation);

                TaskCompletionSource<Stream> tcs = new(cancellation);
                tcs.SetTimeOut(10000, () =>
                {
                    _logger.LogError($"客户端在指定时间内为建立Swap连接 {RequestId}|{host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");
                });

                _fastTunnelServer.ResponseTasks.TryAdd(RequestId, tcs);
                var res = await tcs.Task;
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

        private async ValueTask<Stream> OfflinePage(string host, SocketsHttpConnectionContext context)
        {
            var bytes = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type:text/html; charset=utf-8\r\n\r\n{TunnelResource.Page_Offline}\r\n");

            return await Task.FromResult(new ResponseStream(bytes));
        }
    }
}
