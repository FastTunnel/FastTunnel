using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Utility.Extensions;
using System.Net.WebSockets;
using FastTunnel.Core.Forwarder;
using Microsoft;
using Microsoft.AspNetCore.DataProtection;
using FastTunnel.Core.Server;
using System.Data.Common;

namespace FastTunnel.Core.Handlers.Client
{
    public class HttpRequestHandler : IClientHandler
    {
        ILogger<HttpRequestHandler> _logger;

        public HttpRequestHandler(ILogger<HttpRequestHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync<T>(FastTunnelClient cleint, T Msg) where T : TunnelMassage
        {
            var request = Msg as NewCustomerMassage;
            if (request.MsgId.Contains("_"))
            {
                var interval = long.Parse(DateTime.Now.GetChinaTicks()) - long.Parse(request.MsgId.Split('_')[0]);

                _logger.LogDebug($"Start SwapMassage {request.MsgId} 服务端耗时：{interval}ms");
            }

            //var webSocket = new ClientWebSocket();
            //webSocket.Options.RemoteCertificateValidationCallback = delegate { return true; };
            //webSocket.Options.SetRequestHeader(HeaderConst.FASTTUNNEL_FLAG, "2.0.0");
            //webSocket.Options.SetRequestHeader(HeaderConst.FASTTUNNEL_TYPE, HeaderConst.TYPE_SWAP);

            //var uri = new Uri($"ws://{cleint.Server.ServerAddr}:{cleint.Server.ServerPort}/{request.MsgId}");
            //webSocket.ConnectAsync(uri, CancellationToken.None);

            await Task.Yield();

            var connecter = new DnsSocket(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            connecter.Connect();
            // connecter.Send(new Message<SwapMassage> { MessageType = MessageType.C_SwapMsg, Content = new SwapMassage(request.MsgId) });

            Stream serverConn = new NetworkStream(connecter.Socket, ownsSocket: true);
            var reverse = $"PROXY /{request.MsgId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";

            var requestMsg = Encoding.ASCII.GetBytes(reverse);
            serverConn.WriteAsync(requestMsg, CancellationToken.None).GetAwaiter().GetResult();

            _logger.LogDebug($"连接server成功 {request.MsgId}");
            var localConnecter = new DnsSocket(request.WebConfig.LocalIp, request.WebConfig.LocalPort);

            try
            {
                localConnecter.Connect();
            }
            catch (SocketException sex)
            {
                if (sex.ErrorCode == 10061)
                {
                    _logger.LogInformation($"内网服务不存在：{request.WebConfig.LocalIp}:{request.WebConfig.LocalPort}");
                    // 内网的站点不存在或无法访问
                    //string statusLine = "HTTP/1.1 200 OK\r\n";
                    //string responseHeader = "Content-Type: text/html\r\n";
                    //byte[] responseBody;
                    //responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_NoSite);

                    //connecter.Send(Encoding.UTF8.GetBytes(statusLine));
                    //connecter.Send(Encoding.UTF8.GetBytes(responseHeader));
                    //connecter.Send(Encoding.UTF8.GetBytes("\r\n"));
                    //connecter.Send(responseBody);

                    //connecter.Socket.Disconnect(false);
                    //connecter.Socket.Close();
                    return;
                }
                else
                {
                    throw;
                }
            }
            catch (Exception)
            {
                localConnecter.Close();
                throw;
            }

            _logger.LogDebug($"连接本地成功 {request.MsgId}");
            //var streamServer = new WebSocktReadWriteStream(webSocket);
            //var streamLocal = new SocketReadWriteStream(localConnecter.Socket);

            var localConn = new NetworkStream(localConnecter.Socket, ownsSocket: true);

            _logger.LogDebug($"开始转发 {request.MsgId}");
            var taskX = serverConn.CopyToAsync(localConn, CancellationToken.None);
            var taskY = localConn.CopyToAsync(serverConn, CancellationToken.None);

            await Task.WhenAny(taskX, taskY);

            try
            {
                localConn.Close();
                serverConn.Close();

                _logger.LogDebug($"转发结束 {request.MsgId}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"转发结束 {request.MsgId}");
            }
        }

    }
}
