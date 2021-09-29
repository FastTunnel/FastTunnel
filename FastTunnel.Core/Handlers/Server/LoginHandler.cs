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
        ILogger _logger;
        IProxyConfigProvider proxyConfig;
        public const bool NeedRecive = true;

        public LoginHandler(ILogger<LoginHandler> logger, IProxyConfigProvider proxyConfig)
        {
            this.proxyConfig = proxyConfig;
            this._logger = logger;
        }

        protected async Task HandleLoginAsync(FastTunnelServer server, WebSocket client, LogInMassage requet)
        {
            bool hasTunnel = false;

            await client.SendCmdAsync(MessageType.Log, $"穿透协议 | 映射关系（公网=>内网）", CancellationToken.None);
            Thread.Sleep(300);

            if (requet.Webs != null && requet.Webs.Count() > 0)
            {
                hasTunnel = true;
                foreach (var item in requet.Webs)
                {
                    var hostName = $"{item.SubDomain}.{server.ServerOption.CurrentValue.WebDomain}".Trim();
                    var info = new WebInfo { Socket = client, WebConfig = item };

                    _logger.LogDebug($"new domain '{hostName}'");
                    server.WebList.AddOrUpdate(hostName, info, (key, oldInfo) => { return info; });
                    (proxyConfig as InMemoryConfigProvider).AddWeb(hostName);

                    await client.SendCmdAsync(MessageType.Log, $"  HTTP   | http://{hostName} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);

                    if (item.WWW != null)
                    {
                        foreach (var www in item.WWW)
                        {
                            // TODO:validateDomain
                            _logger.LogInformation($"WWW {www}");

                            server.WebList.AddOrUpdate(www, info, (key, oldInfo) => { return info; });
                            (proxyConfig as InMemoryConfigProvider).AddWeb(www);

                            await client.SendCmdAsync(MessageType.Log, $"  HTTP   | http://{www} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);

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
                                _logger.LogDebug($"Remove Listener {old.Listener.ListenIp}:{old.Listener.ListenPort}");
                                old.Listener.Stop();
                                server.ForwardList.TryRemove(item.RemotePort, out ForwardInfo<ForwardHandlerArg> _);
                            }

                            // TODO: 客户端离线时销毁
                            var ls = new PortProxyListener("0.0.0.0", item.RemotePort, _logger, client);
                            ls.Start(new ForwardDispatcher(_logger, server, item));

                            // TODO: 客户端离线时销毁
                            server.ForwardList.TryAdd(item.RemotePort, new ForwardInfo<ForwardHandlerArg> { Listener = ls, Socket = client, SSHConfig = item });
                            _logger.LogDebug($"SSH proxy success: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");

                            await client.SendCmdAsync(MessageType.Log, $"  TCP    | {server.ServerOption.CurrentValue.WebDomain}:{item.RemotePort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"SSH proxy error: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                            _logger.LogError(ex.Message);
                            await client.SendCmdAsync(MessageType.Log, ex.Message, CancellationToken.None);
                            continue;
                        }
                    }
                }
                else
                {
                    await client.SendCmdAsync(MessageType.Log, TunnelResource.ForwardDisabled, CancellationToken.None);
                }
            }

            if (!hasTunnel)
                await client.SendCmdAsync(MessageType.Log, TunnelResource.NoTunnel, CancellationToken.None);
        }

        public virtual async Task<bool> HandlerMsg(FastTunnelServer server, WebSocket client, string content)
        {
            var msg = JsonSerializer.Deserialize<LogInMassage>(content);
            await HandleLoginAsync(server, client, msg);
            return NeedRecive;
        }
    }
}
