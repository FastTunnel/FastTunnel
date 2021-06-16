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

namespace FastTunnel.Core.Dispatchers
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

        static string pattern = @"[hH]ost:.+[\r\n]";

        public void Dispatch(Socket httpClient)
        {
            Stream tempBuffer = new MemoryStream();

            try
            {
                // 1.检查白名单
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

                //定义byte数组存放从客户端接收过来的数据
                byte[] buffer = new byte[1024]; // 1k

                MatchCollection collection;
                string words = string.Empty;

                try
                {
                    while (true)
                    {
                        var count = httpClient.Receive(buffer);
                        if (count == 0)
                        {
                            httpClient.Close();
                            return;
                        }

                        // 读取的字节缓存到内存
                        tempBuffer.Write(buffer, 0, count);

                        tempBuffer.Seek(0, SeekOrigin.Begin);
                        var array = new byte[tempBuffer.Length];
                        tempBuffer.Read(array, 0, (int)tempBuffer.Length);

                        // 将字节转换成字符串
                        words = Encoding.UTF8.GetString(array, 0, (int)tempBuffer.Length);

                        collection = Regex.Matches(words, pattern);
                        if (collection.Count > 0 || count < buffer.Length)
                        {
                            break;
                        }
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


                string Host;
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
                    HandlerClientNotOnLine(httpClient, domain);
                    return;
                }

                var msgid = Guid.NewGuid().ToString();

                tempBuffer.Seek(0, SeekOrigin.Begin);
                var byteArray = new byte[tempBuffer.Length];
                tempBuffer.Read(byteArray, 0, (int)tempBuffer.Length);

                tempBuffer.Close();
                _fastTunnelServer.RequestTemp.TryAdd(msgid, new NewRequest
                {
                    CustomerClient = httpClient,
                    Buffer = byteArray
                });

                try
                {
                    _logger.LogDebug($"OK");
                    web.Socket.Send(new Message<NewCustomerMassage> { MessageType = MessageType.S_NewCustomer, Content = new NewCustomerMassage { MsgId = msgid, WebConfig = web.WebConfig } });
                }
                catch (Exception)
                {
                    HandlerClientNotOnLine(httpClient, domain);
                    throw;
                }
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

        public void Dispatch(AsyncUserToken token, string words)
        {
            throw new NotImplementedException();
        }
    }
}
