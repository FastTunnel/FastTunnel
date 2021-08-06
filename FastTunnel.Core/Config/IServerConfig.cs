using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public interface IServerConfig
    {
        #region Web相关配置

        int WebProxyPort { get; set; }

        string WebDomain { get; set; }

        string[] WebAllowAccessIps { get; set; }

        #endregion

        bool EnableForward { get; set; }
    }
}
