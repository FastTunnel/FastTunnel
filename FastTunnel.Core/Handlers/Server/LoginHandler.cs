using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Global;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Listener;
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
    public class LoginHandler : IClientMessageHandler
    {
        ILogger _logger;

        public bool NeedRecive => true;
        IConfigHandler _configHandler;

        static object _locker = new object();

        public LoginHandler(ILogger logger)
        {
            _logger = logger;
            var custome = FastTunnelGlobal.GetCustomHandler<IConfigHandler>();
            _configHandler = custome == null ? new ConfigHandler() : custome;
        }

        public LogInMassage GetConfig(JObject content)
        {
            return _configHandler.GetConfig(content);
        }

        public void HandlerMsg(FastTunnelServer server, Socket client, Message<JObject> msg)
        {
            lock (_locker)
            {
                HandleLogin(server, client, GetConfig(msg.Content));
            }
        }

        public void HandleLogin(FastTunnelServer server, Socket client, LogInMassage requet)
        {
            bool hasTunnel = false;

            var filters = FastTunnelGlobal.GetFilters(typeof(IAuthenticationFilter));
            if (filters.Count() > 0)
            {
                foreach (IAuthenticationFilter item in filters)
                {
                    var result = item.Authentication(server, requet);
                    if (!result)
                    {
                        client.SendCmd(new Message<LogMassage>
                        {
                            MessageType = MessageType.Log,
                            Content = new LogMassage(LogMsgType.Error, "认证失败")
                        });

                        return;
                    }
                }
            }

            var sb = new StringBuilder($"{Environment.NewLine}=====隧道已建立成功，可通过以下方式访问内网服务====={Environment.NewLine}{Environment.NewLine}");
            sb.Append($"穿透协议 | 映射关系（公网=>内网）{Environment.NewLine}");
            if (requet.Webs != null && requet.Webs.Count() > 0)
            {
                hasTunnel = true;
                foreach (var item in requet.Webs)
                {
                    var hostName = $"{item.SubDomain}.{server.serverOption.CurrentValue.WebDomain}".Trim();
                    var info = new WebInfo { Socket = client, WebConfig = item };

                    _logger.LogDebug($"new domain '{hostName}'");
                    server.WebList.AddOrUpdate(hostName, info, (key, oldInfo) => { return info; });
                    sb.Append($"  HTTP   | http://{hostName}{(server.serverOption.CurrentValue.WebHasNginxProxy ? string.Empty : ":" + server.serverOption.CurrentValue.WebProxyPort)} => {item.LocalIp}:{item.LocalPort}");
                    sb.Append(Environment.NewLine);
                    if (item.WWW != null)
                    {
                        foreach (var www in item.WWW)
                        {
                            // TODO:validateDomain
                            _logger.LogInformation($"WWW {www}");

                            server.WebList.AddOrUpdate(www, info, (key, oldInfo) => { return info; });
                            sb.Append($"  HTTP   | http://{www}{(server.serverOption.CurrentValue.WebHasNginxProxy ? string.Empty : ":" + server.serverOption.CurrentValue.WebProxyPort)} => {item.LocalIp}:{item.LocalPort}");
                            sb.Append(Environment.NewLine);
                        }
                    }
                }
            }


            if (requet.SSH != null && requet.SSH.Count() > 0)
            {
                hasTunnel = true;

                foreach (var item in requet.SSH)
                {
                    try
                    {
                        if (item.RemotePort.Equals(server.serverOption.CurrentValue.BindPort))
                        {
                            _logger.LogError($"RemotePort can not be same with BindPort: {item.RemotePort}");
                            continue;
                        }

                        if (item.RemotePort.Equals(server.serverOption.CurrentValue.WebProxyPort))
                        {
                            _logger.LogError($"RemotePort can not be same with ProxyPort_HTTP: {item.RemotePort}");
                            continue;
                        }

                        SSHInfo<SSHHandlerArg> old;
                        if (server.SSHList.TryGetValue(item.RemotePort, out old))
                        {
                            _logger.LogDebug($"Remove Listener {old.Listener.ListenIp}:{old.Listener.ListenPort}");
                            old.Listener.Stop();
                            server.SSHList.TryRemove(item.RemotePort, out SSHInfo<SSHHandlerArg> _);
                        }

                        var ls = new PortProxyListener("0.0.0.0", item.RemotePort, _logger);

                        ls.Start(new SSHDispatcher(server, client, item));

                        // listen success
                        server.SSHList.TryAdd(item.RemotePort, new SSHInfo<SSHHandlerArg> { Listener = ls, Socket = client, SSHConfig = item });
                        _logger.LogDebug($"SSH proxy success: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");

                        sb.Append($"  TCP    | {server.serverOption.CurrentValue.WebDomain}:{item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                        sb.Append(Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"SSH proxy error: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                        _logger.LogError(ex.Message);
                        client.SendCmd(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Info, ex.Message) });
                        continue;
                    }
                }
            }

            if (!hasTunnel)
            {
                client.SendCmd(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Info, TunnelResource.NoTunnel) });
            }
            else
            {
                sb.Append($"{Environment.NewLine}====================================================");
                client.SendCmd(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Info, sb.ToString()) });
            }
        }
    }
}
