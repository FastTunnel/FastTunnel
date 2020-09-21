using FastTunnel.Core.Config;
using FastTunnel.Core.Core;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FastTunnel.Core.Handlers.Server
{
    public class HttpDispatcher : IListenerDispatcher
    {
        readonly ILogger _logger;
        readonly IServerConfig _serverSettings;
        readonly FastTunnelServer _fastTunnelServer;

        public HttpDispatcher(FastTunnelServer fastTunnelServer, ILogger logger, IServerConfig serverSettings)
        {
            _logger = logger;
            _serverSettings = serverSettings;
            _fastTunnelServer = fastTunnelServer;
        }

        public void Dispatch(Socket httpClient)
        {
            try
            {
                //定义byte数组存放从客户端接收过来的数据
                byte[] buffer = new byte[1024];

                int count;
                try
                {
                    count = httpClient.Receive(buffer);
                    if (count == 0)
                    {
                        httpClient.Close();
                        return;
                    }
                }
                catch (SocketException ex)
                {
                    _logger.LogError(ex.Message);
                    if (httpClient.Connected)
                        httpClient.Close();
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                    throw;
                }

                try
                {
                    var endpoint = httpClient.RemoteEndPoint as System.Net.IPEndPoint;
                    _logger.LogInformation($"Receive HTTP Request {endpoint.Address}:{endpoint.Port}");

                    if (_serverSettings.WebAllowAccessIps != null)
                    {
                        if (!_serverSettings.WebAllowAccessIps.Contains(endpoint.Address.ToString()))
                        {
                            HandlerHostNotAccess(httpClient);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                }

                //将字节转换成字符串
                string words = Encoding.UTF8.GetString(buffer, 0, count);

                // 正则获取Host
                String Host = string.Empty;
                var pattern = @"[hH]ost:.+";
                var collection = Regex.Matches(words, pattern);
                if (collection.Count == 0)
                {
                    _logger.LogError($"Host异常：{words}");

                    // 返回错误页
                    HandlerHostRequired(httpClient);
                    return;
                }
                else
                {
                    Host = collection[0].Value;
                }

                _logger.LogDebug(Host.Replace("\r", ""));
                var domain = Host.Split(":")[1].Trim();

                // 判断是否为ip
                if (IsIpDomian(domain))
                {
                    // 返回错误页
                    HandlerHostRequired(httpClient);
                    return;
                }

                WebInfo web;
                if (!_fastTunnelServer.WebList.TryGetValue(domain, out web))
                {
                    HandlerClientNotOnLine(httpClient, domain, buffer);
                    return;
                }

                if (!web.Socket.Connected)
                {
                    _fastTunnelServer.WebList.TryRemove(domain, out WebInfo invalidWeb);
                    HandlerClientNotOnLine(httpClient, domain, buffer);
                    return;
                }

                var msgid = Guid.NewGuid().ToString();

                byte[] bytes = new byte[count];
                Array.Copy(buffer, bytes, count);

                _fastTunnelServer.newRequest.TryAdd(msgid, new NewRequest
                {
                    CustomerClient = httpClient,
                    Buffer = bytes
                });

                _logger.LogDebug($"OK");
                web.Socket.Send(new Message<NewCustomerMassage> { MessageType = MessageType.S_NewCustomer, Content = new NewCustomerMassage { MsgId = msgid, WebConfig = web.WebConfig } });
            }
            catch (Exception ex)
            {
                _logger.LogError("处理Http失败：" + ex);
                httpClient.Close();
            }
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

        private void HandlerClientNotOnLine(Socket client, string domain, byte[] buffer)
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
    }
}
