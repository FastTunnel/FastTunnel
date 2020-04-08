using FastTunnel.Core.Core;
using FastTunnel.Core.Extensions;
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
    public class LoginHandler : IServerHandler
    {
        ILogger<LoginHandler> _logger;

        public LoginHandler(ILogger<LoginHandler> logger)
        {
            _logger = logger;
        }

        public LogInRequest GetConfig(JObject content)
        {
            return content.ToObject<LogInRequest>();
        }

        public void HandlerMsg(FastTunnelServer server, Socket client, Message<JObject> msg)
        {
            HandleLogin(server, client, GetConfig(msg.Content));
            server.ReceiveClient(client, null);
        }

        public void HandleLogin(FastTunnelServer server, Socket client, LogInRequest requet)
        {
            if (requet.Webs != null && requet.Webs.Count() > 0)
            {
                foreach (var item in requet.Webs)
                {
                    var hostName = $"{item.SubDomain}.{server._serverSettings.Domain}".Trim();
                    if (server.WebList.ContainsKey(hostName))
                    {
                        _logger.LogDebug($"renew domain '{hostName}'");

                        server.WebList.Remove(hostName);
                        server.WebList.Add(hostName, new WebInfo { Socket = client, WebConfig = item });
                    }
                    else
                    {
                        _logger.LogDebug($"new domain '{hostName}'");
                        server.WebList.Add(hostName, new WebInfo { Socket = client, WebConfig = item });
                    }

                    client.Send(new Message<LogMsg> { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Info, $"TunnelForWeb is OK: you can visit {item.LocalIp}:{item.LocalPort} from http://{hostName}:{server._serverSettings.ProxyPort_HTTP}") });
                }

                client.Send(new Message<LogMsg> { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Info, "web隧道已建立完毕") });
            }

            if (requet.SSH != null && requet.SSH.Count() > 0)
            {
                foreach (var item in requet.SSH)
                {
                    try
                    {
                        if (item.RemotePort.Equals(server._serverSettings.BindPort))
                        {
                            _logger.LogError($"RemotePort can not be same with BindPort: {item.RemotePort}");
                            continue;
                        }

                        if (item.RemotePort.Equals(server._serverSettings.ProxyPort_HTTP))
                        {
                            _logger.LogError($"RemotePort can not be same with ProxyPort_HTTP: {item.RemotePort}");
                            continue;
                        }

                        SSHInfo<SSHHandlerArg> old;
                        if (server.SSHList.TryGetValue(item.RemotePort, out old))
                        {
                            _logger.LogDebug($"Remove Listener {old.Listener.IP}:{old.Listener.Port}");
                            old.Listener.ShutdownAndClose();
                            server.SSHList.Remove(item.RemotePort);
                        }

                        var ls = new Listener<SSHHandlerArg>("0.0.0.0", item.RemotePort, _logger,

                            (client, local) =>
                            {
                                var msgid = Guid.NewGuid().ToString();
                                local.LocalClient.Send(new Message<NewSSHRequest> { MessageType = MessageType.S_NewSSH, Content = new NewSSHRequest { MsgId = msgid, SSHConfig = local.SSHConfig } });

                                server.newRequest.Add(msgid, new NewRequest
                                {
                                    CustomerClient = client,
                                });
                            }
                            , new SSHHandlerArg { LocalClient = client, SSHConfig = item });
                        ls.Listen();

                        // listen success
                        server.SSHList.Add(item.RemotePort, new SSHInfo<SSHHandlerArg> { Listener = ls, Socket = client, SSHConfig = item });
                        _logger.LogDebug($"SSH proxy success: {item.RemotePort} -> {item.LocalIp}:{item.LocalPort}");

                        client.Send(new Message<LogMsg> { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Info, $"TunnelForSSH is OK: [ServerAddr]:{item.RemotePort}->{item.LocalIp}:{item.LocalPort}") });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"SSH proxy error: {item.RemotePort} -> {item.LocalIp}:{item.LocalPort}");
                        _logger.LogError(ex.Message);
                        client.Send(new Message<LogMsg> { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Info, ex.Message) });
                        continue;
                    }
                }

                client.Send(new Message<LogMsg> { MessageType = MessageType.Log, Content = new LogMsg(LogMsgType.Info, "远程桌面隧道已建立完毕") });
            }
        }
    }
}
