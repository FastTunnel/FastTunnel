using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.IO;
using FastTunnel.Core.Server;
using System.Diagnostics;

namespace FastTunnel.Core.Dispatchers
{
    public class HttpDispatcherV2 : IListenerDispatcher
    {
        readonly ILogger _logger;
        readonly IServerConfig _serverSettings;
        readonly FastTunnelServer _fastTunnelServer;

        public HttpDispatcherV2(FastTunnelServer fastTunnelServer, ILogger logger, IServerConfig serverSettings)
        {
            _logger = logger;
            _serverSettings = serverSettings;
            _fastTunnelServer = fastTunnelServer;
        }

        static string pattern = @"[hH]ost:.+";

        public void Dispatch(AsyncUserToken token, string words)
        {
            _logger.LogDebug($"=======Dispatch HTTP {token.RequestId}========");

            Stopwatch sw = new Stopwatch();
            sw.Start();

            // 1.检查白名单
            try
            {
                var endpoint = token.Socket.RemoteEndPoint as System.Net.IPEndPoint;
                _logger.LogInformation($"Receive HTTP Request {endpoint.Address}:{endpoint.Port}");

                if (_serverSettings.WebAllowAccessIps != null)
                {
                    if (!_serverSettings.WebAllowAccessIps.Contains(endpoint.Address.ToString()))
                    {
                        HandlerHostNotAccess(token.Socket);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }

            string Host;
            MatchCollection collection = Regex.Matches(words, pattern);
            if (collection.Count == 0)
            {
                _logger.LogError($"【Host异常】：{words}");

                // 返回错误页
                HandlerHostRequired(token.Socket);
                return;
            }
            else
            {
                Host = collection[0].Value;
            }

            _logger.LogDebug(Host.Replace("\r", ""));
            var domain = Host.Split(":")[1].Trim();

            _logger.LogDebug($"=======Dispatch domain:{domain} {token.RequestId} ========");

            // 判断是否为ip
            if (IsIpDomian(domain))
            {
                // 返回错误页
                HandlerHostRequired(token.Socket);
                return;
            }

            WebInfo web;
            if (!_fastTunnelServer.WebList.TryGetValue(domain, out web))
            {
                _logger.LogDebug($"=======站点未登录 {token.RequestId}========");
                HandlerClientNotOnLine(token.Socket, domain);
                return;
            }

            _logger.LogDebug($"=======找到映射的站点 {token.RequestId}========");
            _fastTunnelServer.RequestTemp.TryAdd(token.RequestId, new NewRequest
            {
                CustomerClient = token.Socket,
                Buffer = token.Recived
            });

            try
            {
                sw.Stop();
                _logger.LogDebug($"[寻找路由耗时]：{sw.ElapsedMilliseconds}ms");

                sw.Restart();
                web.Socket.SendCmd(new Message<NewCustomerMassage> { MessageType = MessageType.S_NewCustomer, Content = new NewCustomerMassage { MsgId = token.RequestId, WebConfig = web.WebConfig } });

                sw.Stop();
                _logger.LogDebug($"[发送NewCustomer指令耗时]：{sw.ElapsedMilliseconds}");

                _logger.LogDebug($"=======发送请求成功 {token.RequestId}========");
            }
            catch (Exception)
            {
                _logger.LogDebug($"=======客户端不在线 {token.RequestId}========");
                HandlerClientNotOnLine(token.Socket, domain);

                // 移除
                _fastTunnelServer.WebList.TryRemove(domain, out _);
            }
        }

        public void Dispatch(Socket httpClient)
        {
            throw new NotImplementedException();
        }

        private bool IsIpDomian(string domain)
        {
            return Regex.IsMatch(domain, @"^\d.\d.\d.\d.\d$");
        }

        private void HandlerHostNotAccess(Socket client)
        {
            _logger.LogDebug($"### NotAccessIps:'{client.RemoteEndPoint}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";

            byte[] responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_NotAccessIps);

            client.Send(Encoding.UTF8.GetBytes(statusLine));
            client.Send(Encoding.UTF8.GetBytes(responseHeader));
            client.Send(Encoding.UTF8.GetBytes("\r\n"));
            client.Send(responseBody);
            client.Close();
        }

        private void HandlerHostRequired(Socket client)
        {
            _logger.LogDebug($"### HostRequired:'{client.RemoteEndPoint}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";

            byte[] responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_HostRequired);

            client.Send(Encoding.UTF8.GetBytes(statusLine));
            client.Send(Encoding.UTF8.GetBytes(responseHeader));
            client.Send(Encoding.UTF8.GetBytes("\r\n"));
            client.Send(responseBody);
            client.Close();
        }

        private void HandlerClientNotOnLine(Socket client, string domain)
        {
            _logger.LogDebug($"### TunnelNotFound:'{domain}'");
            string statusLine = "HTTP/1.1 200 OK\r\n";
            string responseHeader = "Content-Type: text/html\r\n";

            byte[] responseBody = Encoding.UTF8.GetBytes(TunnelResource.Page_NoTunnel);

            client.Send(Encoding.UTF8.GetBytes(statusLine));
            client.Send(Encoding.UTF8.GetBytes(responseHeader));
            client.Send(Encoding.UTF8.GetBytes("\r\n"));
            client.Send(responseBody);
            client.Close();
        }

        public void Dispatch(Socket httpClient, Action<Socket> onOffLine)
        {
            Dispatch(httpClient);
        }
    }
}
