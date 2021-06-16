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

        static string pattern = @"[hH]ost:.+[\r\n]";

        public void Dispatch(AsyncUserToken token, string words)
        {
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
                _logger.LogError($"Host异常：{words}");

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
                HandlerClientNotOnLine(token.Socket, domain);
                return;
            }

            var msgid = Guid.NewGuid().ToString();
            _fastTunnelServer.RequestTemp.TryAdd(msgid, new NewRequest
            {
                CustomerClient = token.Socket,
                Buffer = token.Recived
            });

            try
            {
                _logger.LogDebug($"OK");
                web.Socket.Send(new Message<NewCustomerMassage> { MessageType = MessageType.S_NewCustomer, Content = new NewCustomerMassage { MsgId = msgid, WebConfig = web.WebConfig } });
            }
            catch (Exception)
            {
                HandlerClientNotOnLine(token.Socket, domain);
                throw;
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
