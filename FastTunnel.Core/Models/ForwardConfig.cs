using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class ForwardConfig
    {
        /// <summary>
        /// 局域网IP地址
        /// </summary>
        public string LocalIp { get; set; }

        /// <summary>
        /// 局域网ssh端口号
        /// </summary>
        public int LocalPort { get; set; } = 22;

        /// <summary>
        /// 服务端监听的端口号 1~65535
        /// </summary>
        public int RemotePort { get; set; }

        /// <summary>
        /// 协议，内网服务监听的协议
        /// </summary>
        public ProtocolEnum Protocol { get; set; }
    }

    public enum ProtocolEnum
    {
        TCP = 0,

        UDP = 1,
    }
}
