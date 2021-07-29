using FastTunnel.Core.Config;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class LogInMassage : TunnelMassage
    {
        /// <summary>
        /// web穿透隧道列表
        /// </summary>
        public IEnumerable<WebConfig> Webs { get; set; }

        /// <summary>
        /// 端口转发隧道列表
        /// </summary>
        public IEnumerable<SSHConfig> SSH { get; set; }

        /// <summary>
        /// 客户端附加信息
        /// </summary>
        public string Token { get; set; }
    }
}
