using FastTunnel.Core.Client;
using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Configuration;
using Yarp.Sample;

namespace FastTunnel.Core.Handlers.Server
{
    public class LoginHandler : ILoginHandler
    {
        ILogger logger;
        IProxyConfigProvider proxyConfig;
        public const bool NeedRecive = true;

        public LoginHandler(ILogger<LoginHandler> logger, IProxyConfigProvider proxyConfig)
        {
            this.proxyConfig = proxyConfig;
            this.logger = logger;
        }

        protected async Task HandleLoginAsync(FastTunnelServer server, TunnelClient client, LogInMassage requet)
        {
            bool hasTunnel = false;

            await client.webSocket.SendCmdAsync(MessageType.Log, $"穿透协议 | 映射关系（公网=>内网）", CancellationToken.None);
            Thread.Sleep(300);

            if (requet.Webs != null && requet.Webs.Count() > 0)
            {
                hasTunnel = true;
                foreach (var item in requet.Webs)
                {
                    var hostName = $"{item.SubDomain}.{server.ServerOption.CurrentValue.WebDomain}".Trim().ToLower();
                    var info = new WebInfo { Socket = client.webSocket, WebConfig = item };

                    logger.LogDebug($"new domain '{hostName}'");
                    server.WebList.AddOrUpdate(hostName, info, (key, oldInfo) => { return info; });
                    (proxyConfig as InMemoryConfigProvider).AddWeb(hostName);

                    await client.webSocket.SendCmdAsync(MessageType.Log, $"  HTTP   | http://{hostName}:{client.ConnectionPort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                    client.AddWeb(info);

                    if (item.WWW != null)
                    {
                        foreach (var www in item.WWW)
                        {
                            // TODO:validateDomain
                            hostName = www.Trim().ToLower();
                            server.WebList.AddOrUpdate(www, info, (key, oldInfo) => { return info; });
                            (proxyConfig as InMemoryConfigProvider).AddWeb(www);

                            await client.webSocket.SendCmdAsync(MessageType.Log, $"  HTTP   | http://{www}:{client.ConnectionPort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                            client.AddWeb(info);
                        }
                    }
                }
            }

            if (requet.Forwards != null && requet.Forwards.Count() > 0)
            {
                if (server.ServerOption.CurrentValue.EnableForward)
                {
                    hasTunnel = true;

                    foreach (var item in requet.Forwards)
                    {
                        try
                        {
                            ForwardInfo<ForwardHandlerArg> old;
                            if (server.ForwardList.TryGetValue(item.RemotePort, out old))
                            {
                                logger.LogDebug($"Remove Listener {old.Listener.ListenIp}:{old.Listener.ListenPort}");
                                old.Listener.Stop();
                                server.ForwardList.TryRemove(item.RemotePort, out ForwardInfo<ForwardHandlerArg> _);
                            }

                            // TODO: 客户端离线时销毁
                            var ls = new PortProxyListener("0.0.0.0", item.RemotePort, logger, client.webSocket);
                            ls.Start(new ForwardDispatcher(logger, server, item));

                            var forwardInfo = new ForwardInfo<ForwardHandlerArg> { Listener = ls, Socket = client.webSocket, SSHConfig = item };

                            // TODO: 客户端离线时销毁
                            server.ForwardList.TryAdd(item.RemotePort, forwardInfo);
                            logger.LogDebug($"SSH proxy success: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");

                            client.AddForward(forwardInfo);
                            await client.webSocket.SendCmdAsync(MessageType.Log, $"  TCP    | {server.ServerOption.CurrentValue.WebDomain}:{item.RemotePort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"SSH proxy error: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                            logger.LogError(ex.Message);
                            await client.webSocket.SendCmdAsync(MessageType.Log, ex.Message, CancellationToken.None);
                            continue;
                        }
                    }
                }
                else
                {
                    await client.webSocket.SendCmdAsync(MessageType.Log, TunnelResource.ForwardDisabled, CancellationToken.None);
                }
            }

            if (!hasTunnel)
                await client.webSocket.SendCmdAsync(MessageType.Log, TunnelResource.NoTunnel, CancellationToken.None);
        }

        public virtual async Task<bool> HandlerMsg(FastTunnelServer fastTunnelServer, TunnelClient tunnelClient, string lineCmd)
        {
            var msg = JsonSerializer.Deserialize<LogInMassage>(lineCmd);
            await HandleLoginAsync(fastTunnelServer, tunnelClient, msg);
            return NeedRecive;
        }
    }
}
