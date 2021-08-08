using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public interface IServerConfig
    {
        string WebDomain { get; set; }

        string[] WebAllowAccessIps { get; set; }

        bool EnableForward { get; set; }
    }
}
