using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Utility.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastTunnel.Core.Sockets
{
    public class SocketSwap : ISocketSwap
    {
        private readonly Socket m_sockt1;
        private readonly Socket m_sockt2;
        private readonly string m_msgId = null;
        private readonly ILogger m_logger;

        private bool swapeStarted = false;

        private class Channel
        {
            public Socket Send { get; set; }

            public Socket Receive { get; set; }
        }

        public SocketSwap(Socket sockt1, Socket sockt2, ILogger logger, string msgId)
        {
            //sockt1.NoDelay = true;
            //sockt2.NoDelay = true;
            m_sockt1 = sockt1;
            m_sockt2 = sockt2;
            m_msgId = msgId;
            m_logger = logger;
        }

        public void StartSwap()
        {
            m_logger?.LogDebug($"[StartSwapStart] {m_msgId}");
            swapeStarted = true;

            ThreadPool.QueueUserWorkItem(swapCallback, new Channel
            {
                Send = m_sockt1,
                Receive = m_sockt2
            });

            ThreadPool.QueueUserWorkItem(swapCallback, new Channel
            {
                Send = m_sockt2,
                Receive = m_sockt1
            });

            m_logger?.LogDebug($"[StartSwapEnd] {m_msgId}");
        }

        private void swapCallback(object state)
        {
            m_logger?.LogDebug($"swapCallback {m_msgId}");
            var chanel = state as Channel;
            byte[] result = new byte[512];

            while (true)
            {
                int num;

                try
                {
                    try
                    {
                        num = chanel.Receive.Receive(result, 0, result.Length, SocketFlags.None);
                    }
                    catch (Exception)
                    {
                        closeSocket("Revice Fail");
                        break;
                    }

                    if (num == 0)
                    {
                        closeSocket("Normal Close");
                        break;
                    }

                    try
                    {
                        chanel.Send.Send(result, 0, num, SocketFlags.None);
                    }
                    catch (Exception)
                    {
                        closeSocket("Send Fail");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    m_logger.LogCritical(ex, "致命异常");
                    break;
                }
            }

            var interval = long.Parse(DateTime.Now.GetChinaTicks()) - long.Parse(m_msgId.Split('_')[0]);
            m_logger?.LogDebug($"endSwap {m_msgId} 交互时常：{interval}ms");
        }

        private void closeSocket(string msg)
        {
            m_logger.LogDebug($"【closeSocket】：{msg}");

            try
            {
                m_sockt1.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }
            finally
            {
                m_sockt1.Close();
            }

            try
            {
                m_sockt2.Shutdown(SocketShutdown.Both);

            }
            catch (Exception)
            {
            }
            finally
            {
                m_sockt2.Close();
            }

        }

        public ISocketSwap BeforeSwap(Action fun)
        {
            m_logger?.LogDebug($"BeforeSwap {m_msgId}");

            if (swapeStarted)
            {
                throw new Exception("BeforeSwap must be invoked before StartSwap!");
            }

            fun?.Invoke();
            return this;
        }
    }
}