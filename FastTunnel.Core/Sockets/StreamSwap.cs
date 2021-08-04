using FastTunnel.Core.Handlers.Client;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets
{
    public class StreamSwap
    {
        private Stream stream1;
        private Stream stream2;
        private ILogger<ForwardHandler> logger;
        private string msgId;

        public StreamSwap(Stream serverConnection, NetworkStream localConn, ILogger<ForwardHandler> logger, string msgId)
        {
            this.stream1 = serverConnection;
            this.stream2 = localConn;

            this.logger = logger;
            this.msgId = msgId;
        }

        public async Task StartSwapAsync()
        {
            logger.LogDebug($"[StartSwapStart] {msgId}");
            var task = new Task(() =>
            {
                work(stream1, stream2);
            });

            var task1 = new Task(() =>
            {
                work(stream2, stream1);
            });

            await Task.WhenAll(task1, task);

            logger.LogDebug($"[StartSwapEnd] {msgId}");
        }

        private void work(Stream streamRevice, Stream streamSend)
        {
            byte[] buffer = new byte[512];

            while (true)
            {
                int num;

                try
                {
                    try
                    {
                        num = streamRevice.Read(buffer);
                        Console.WriteLine($"{Encoding.UTF8.GetString(buffer, 0, num)}");
                    }
                    catch (Exception)
                    {
                        close("Revice Fail");
                        break;
                    }

                    if (num == 0)
                    {
                        close("Normal Close");
                        break;
                    }

                    try
                    {
                        streamSend.Write(buffer, 0, num);
                    }
                    catch (Exception)
                    {
                        close("Send Fail");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "致命异常");
                    break;
                }
            }
        }

        private void close(string msg)
        {
            logger.LogError($"Sarp Error {msg}");

        }
    }
}
