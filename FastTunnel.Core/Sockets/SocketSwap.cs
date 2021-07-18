using FastTunnel.Core.Dispatchers;
using FastTunnel.Core.Utility.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
            m_sockt1 = sockt1;
            m_sockt2 = sockt2;
            m_msgId = msgId;
            m_logger = logger;
        }

        public void StartSwap()
        {
            var st1 = new NetworkStream(m_sockt1, ownsSocket: true);
            var st2 = new NetworkStream(m_sockt2, ownsSocket: true);

            var taskX = st1.CopyToAsync(st2);
            var taskY = st2.CopyToAsync(st1);

            Task.WhenAny(taskX, taskY);
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