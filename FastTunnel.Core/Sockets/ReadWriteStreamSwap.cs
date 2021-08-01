using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using FastTunnel.Core.Forwarder;

namespace FastTunnel.Core.Sockets
{
    public class ReadWriteStreamSwap
    {
        IReadWriteStream stream;
        IReadWriteStream stream1;
        ILogger logger;
        string msgId;

        public ReadWriteStreamSwap(IReadWriteStream stream, IReadWriteStream stream1, ILogger logger, string msgId)
        {
            this.stream = stream;
            this.stream1 = stream1;

            this.logger = logger;
            this.msgId = msgId;
        }

        public async Task StartSwapAsync()
        {
            logger.LogDebug($"[StartSwapStart] {msgId}");
            var task = new Task(() =>
            {
                work(stream, stream1);
            });

            var task1 = new Task(() =>
            {
                work(stream1, stream);
            });

            await Task.WhenAll(task1, task);
            logger.LogDebug($"[StartSwapEnd] {msgId}");
        }

        private void work(IReadWriteStream streamRevice, IReadWriteStream streamSend)
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
