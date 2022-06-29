// Copyright (c) 2019-2022 Gui.H. https://github.com/FastTunnel/FastTunnel
// The FastTunnel licenses this file to you under the Apache License Version 2.0.
// For more details,You may obtain License file at: https://github.com/FastTunnel/FastTunnel/blob/v2/LICENSE

using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Sockets;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Yarp.ReverseProxy.Forwarder;

namespace FastTunnel.Core.Forwarder
{
    public class FastTunnelForwarderHttpClientFactory : ForwarderHttpClientFactory
    {
        readonly ILogger<FastTunnelForwarderHttpClientFactory> logger;
        readonly FastTunnelServer fastTunnelServer;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public FastTunnelForwarderHttpClientFactory(
            ILogger<FastTunnelForwarderHttpClientFactory> logger,
            IHttpContextAccessor httpContextAccessor, FastTunnelServer fastTunnelServer)
        {
            this.fastTunnelServer = fastTunnelServer;
            this.logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
        {
            base.ConfigureHandler(context, handler);
            handler.ConnectCallback = ConnectCallback;
        }

        private async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var host = context.InitialRequestMessage.RequestUri.Host;

            var contextRequest = _httpContextAccessor.HttpContext;
            //var lifetime = contextRequest.Features.Get<IConnectionLifetimeFeature>()!;

            contextRequest.RequestAborted.Register(() =>
            {
                logger.LogDebug($"[ConnectionClosed]");
            });

            try
            {
                var res = await proxyAsync(host, context, contextRequest.RequestAborted);
                return res;
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async ValueTask<Stream> proxyAsync(string host, SocketsHttpConnectionContext context, CancellationToken cancellation)
        {
            WebInfo web;
            if (!fastTunnelServer.WebList.TryGetValue(host, out web))
            {
                // 客户端已离线
                return await OfflinePage(host, context);
            }

            var msgId = Guid.NewGuid().ToString().Replace("-", "");

            TaskCompletionSource<Stream> tcs = new();
            logger.LogDebug($"[Http]Swap开始 {msgId}|{host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");

            cancellation.Register(() =>
            {
                logger.LogDebug($"[Proxy TimeOut]:{msgId}");
                tcs.TrySetCanceled();
            });

            fastTunnelServer.ResponseTasks.TryAdd(msgId, (tcs, cancellation));

            try
            {
                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{msgId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", cancellation);
                var res = await tcs.Task.WaitAsync(cancellation);

                logger.LogDebug($"[Http]Swap OK {msgId}");
                return res;
            }
            catch (WebSocketException)
            {
                // 通讯异常，返回客户端离线
                return await OfflinePage(host, context);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                fastTunnelServer.ResponseTasks.TryRemove(msgId, out _);
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
