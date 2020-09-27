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
        /// 身份信息，用于服务端认证
        /// </summary>
        public string AuthInfo { get; set; }
    }
}
