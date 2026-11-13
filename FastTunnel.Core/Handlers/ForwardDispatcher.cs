// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Models;
using FastTunnel.Core.Utilitys;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers
{
    public class ForwardDispatcher
    {
        private FastTunnelServer _server;
        private ForwardConfig _config;
        ILogger logger;

        public ForwardDispatcher(ILogger logger, FastTunnelServer server, ForwardConfig config)
        {
            this.logger = logger;
            _server = server;
            _config = config;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="_socket">用户请求</param>
        /// <param name="client">FastTunnel客户端</param>
        /// <returns></returns>
        public async Task DispatchAsync(Socket _socket, WebSocket client, PortProxyListener listener)
        {
            var msgId = Guid.NewGuid().ToString().Replace("-", "");

            try
            {
                await Task.Yield();
                logger.LogDebug($"[Forward]Swap开始 {msgId}|{_config.RemotePort}=>{_config.LocalIp}:{_config.LocalPort}");

                // 从这里开始 _socket 归 userStream 管：下面任何 return / 异常都会 dispose 掉它，
                // 不会再出现"没人负责关"的已 accept 连接。
                using var userStream = new NetworkStream(_socket, true) { ReadTimeout = 1000 * 60 * 10 };

                var tcs = new TaskCompletionSource<Stream>();

                _server.ResponseTasks.TryAdd(msgId, (tcs, CancellationToken.None));

                try
                {
                    await client.SendCmdAsync(MessageType.Forward, $"{msgId}|{ForwardAddress.Format(_config.LocalIp, _config.LocalPort)}", CancellationToken.None);
                }
                catch (SocketClosedException sex)
                {
                    // TODO:客户端已掉线，但是没有移除对端口的监听
                    logger.LogError($"[Forward]Swap 客户端已离线 {sex.Message}");
                    Close(_socket);
                    return;
                }
                catch (Exception ex)
                {
                    // 网络不稳定
                    logger.LogError(ex, $"[Forward]Swap Exception");
                    Close(_socket);
                    return;
                }

                Stream tunnelStream;
                try
                {
                    tunnelStream = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    // 隧道没建起来（超时/客户端离线）：断开用户连接，别让他对着一个永远收不到数据的 socket 干等。
                    logger.LogDebug($"[Forward]Swap隧道未建立 {msgId}：" + ex.Message);
                    Close(_socket);
                    return;
                }

                using (tunnelStream)
                {
                    await Task.WhenAny(tunnelStream.CopyToAsync(userStream), userStream.CopyToAsync(tunnelStream));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug($"[Forward]Swap Error {msgId}：" + ex.Message);
            }
            finally
            {
                // 谁把条目从字典里取回来，谁才有资格判定这条隧道的生死：取回来了说明客户端还没接管，
                // 取消它；取不来说明 SwapHandler 已经拿到隧道，关它的责任在那边。
                if (_server.ResponseTasks.TryRemove(msgId, out var owned))
                {
                    owned.Item1.TrySetCanceled();
                }
                logger.LogDebug($"[Forward]Swap结束 {msgId}");
                listener.DecrementClients();
            }
        }

        private void Close(Socket socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }
            finally
            {
                socket.Close();
            }
        }

        /// <summary>
        /// Open a tunnel stream towards the FastTunnel client for UDP forwarding.
        /// The protocol marker "udp" is included so the client knows it must
        /// connect to the local target via UDP and frame datagrams.
        /// </summary>
        public async Task<Stream> RequestUdpTunnelAsync(WebSocket client, CancellationToken cancellationToken)
        {
            var msgId = Guid.NewGuid().ToString().Replace("-", "");
            var tcs = new TaskCompletionSource<Stream>();
            _server.ResponseTasks.TryAdd(msgId, (tcs, cancellationToken));

            try
            {
                logger.LogDebug($"[Forward-UDP]Swap开始 {msgId}|{_config.RemotePort}=>{_config.LocalIp}:{_config.LocalPort}");
                await client.SendCmdAsync(MessageType.Forward, $"{msgId}|udp|{ForwardAddress.Format(_config.LocalIp, _config.LocalPort)}", cancellationToken);
                return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"[Forward-UDP]Swap failed {msgId}");
                _server.ResponseTasks.TryRemove(msgId, out _);
                tcs.TrySetCanceled();
                return null;
            }
        }
    }
}
