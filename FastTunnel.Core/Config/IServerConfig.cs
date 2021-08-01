using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public interface IServerConfig
    {
        // int BindPort { get; set; }

        #region Web相关配置

        int WebProxyPort { get; set; }

        string WebDomain { get; set; }

        /// <summary>
        /// 可选项
        /// 当前服务器是否开启了80端口转发至ProxyPort_HTTP的配置
        /// </summary>
        bool WebHasNginxProxy { get; set; }

        /// <summary>
        /// 可选项
        /// 访问web白名单
        /// </summary>
        string[] WebAllowAccessIps { get; set; }

        #endregion

        bool EnableForward { get; set; }
    }
}
