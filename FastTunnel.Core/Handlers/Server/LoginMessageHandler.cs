using FastTunnel.Core.Core;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace FastTunnel.Core.Handlers
{
    public class LoginMessageHandler : IClientMessageHandler
    {
        ILogger _logger;

        public bool NeedRecive => true;
        IConfigHandler _configHandler;

        public LoginMessageHandler(ILogger logger)
        {
            _logger = logger;
            _configHandler = new ConfigHandler();
        }

        public LogInMassage GetConfig(JObject content)
        {
            return _configHandler.GetConfig(content);
        }

        public void HandlerMsg(FastTunnelServer server, Socket client, Message<JObject> msg)
        {
            HandleLogin(server, client, GetConfig(msg.Content));
        }

        public void HandleLogin(FastTunnelServer server, Socket client, LogInMassage requet)
        {
            bool hasTunnel = false;

            var sb = new StringBuilder($"{Environment.NewLine}=====隧道已建立成功，可通过以下方式访问内网服务====={Environment.NewLine}");
            if (requet.Webs != null && requet.Webs.Count() > 0)
            {
                hasTunnel = true;
                foreach (var item in requet.Webs)
                {
                    var hostName = $"{item.SubDomain}.{server.ServerSettings.WebDomain}".Trim();
                    if (server.WebList.ContainsKey(hostName))
                    {
                        _logger.LogDebug($"renew domain '{hostName}'");

                        server.WebList.TryRemove(hostName, out WebInfo web);
                        server.WebList.TryAdd(hostName, new WebInfo { Socket = client, WebConfig = item });
                    }
                    else
                    {
                        _logger.LogDebug($"new domain '{hostName}'");
                        server.WebList.TryAdd(hostName, new WebInfo { Socket = client, WebConfig = item });
                    }

                    sb.Append($"{Environment.NewLine}  http://{hostName}{(server.ServerSettings.WebHasNginxProxy ? string.Empty : ":" + server.ServerSettings.WebProxyPort)} => {item.LocalIp}:{item.LocalPort}");
                }
            }


            if (requet.SSH != null && requet.SSH.Count() > 0)
            {
                hasTunnel = true;

                foreach (var item in requet.SSH)
                {
                    try
                    {
                        if (item.RemotePort.Equals(server.ServerSettings.BindPort))
                        {
                            _logger.LogError($"RemotePort can not be same with BindPort: {item.RemotePort}");
                            continue;
                        }

                        if (item.RemotePort.Equals(server.ServerSettings.WebProxyPort))
                        {
                            _logger.LogError($"RemotePort can not be same with ProxyPort_HTTP: {item.RemotePort}");
                            continue;
                        }

                        SSHInfo<SSHHandlerArg> old;
                        if (server.SSHList.TryGetValue(item.RemotePort, out old))
                        {
                            _logger.LogDebug($"Remove Listener {old.Listener.IP}:{old.Listener.Port}");
                            old.Listener.ShutdownAndClose();
                            server.SSHList.TryRemove(item.RemotePort, out SSHInfo<SSHHandlerArg> _);
                        }

                        var ls = new AsyncListener("0.0.0.0", item.RemotePort, _logger);

                        ls.Listen(new SSHDispatcher(server, client, item));

                        // listen success
                        server.SSHList.TryAdd(item.RemotePort, new SSHInfo<SSHHandlerArg> { Listener = ls, Socket = client, SSHConfig = item });
                        _logger.LogDebug($"SSH proxy success: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");

                        sb.Append($"{Environment.NewLine}  {server.ServerSettings.WebDomain}:{item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"SSH proxy error: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                        _logger.LogError(ex.Message);
                        client.Send(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Info, ex.Message) });
                        continue;
                    }
                }
            }

            if (!hasTunnel)
            {
                client.Send(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Info, TunnelResource.NoTunnel) });
            }
            else
            {
                sb.Append($"{Environment.NewLine}{Environment.NewLine}====================================================");
                client.Send(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Info, sb.ToString()) });
            }
        }
    }
}
