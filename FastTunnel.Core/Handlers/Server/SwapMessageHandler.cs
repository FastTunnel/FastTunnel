using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Sockets;
using FastTunnel.Core.Utility.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Server
{
    public class SwapMessageHandler : IClientMessageHandler
    {
        public bool NeedRecive => false;

        ILogger _logger;

        public SwapMessageHandler(ILogger logger)
        {
            _logger = logger;
        }

        public void HandlerMsg(FastTunnelServer server, Socket client, Message<JObject> msg)
        {
            var SwapMsg = msg.Content.ToObject<SwapMassage>();
            if (SwapMsg.msgId.Contains("_"))
            {
                var interval = long.Parse(DateTime.Now.GetChinaTicks()) - long.Parse(SwapMsg.msgId.Split('_')[0]);
                _logger.LogDebug($"[开始转发HTTP]：{SwapMsg.msgId} 客户端耗时：{interval}ms");
            }

            if (!string.IsNullOrEmpty(SwapMsg.msgId) && server.ResponseTasks.TryGetValue(SwapMsg.msgId, out var response))
            {
                server.ResponseTasks.TryRemove(SwapMsg.msgId, out _);

                _logger.LogDebug($"SwapMassage：{SwapMsg.msgId}");

                response.SetResult(new NetworkStream(client, true));
            }
            else
            {
                // 未找到，关闭连接
                _logger.LogError($"未找到请求:{SwapMsg.msgId}");

                client.SendCmd(new Message<LogMassage> { MessageType = MessageType.Log, Content = new LogMassage(LogMsgType.Debug, $"未找到请求:{SwapMsg.msgId}") });

                try
                {
                    client.Shutdown(SocketShutdown.Both);
                }
                catch (Exception)
                {
                }

                client.Close();
            }
        }
    }
}
