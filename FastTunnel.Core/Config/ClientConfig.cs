using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class ClientConfig
    {
        public ServerCommon Common { get; set; }

        public IEnumerable<WebConfig> Webs { get; set; }

        public IEnumerable<SSHConfig> SSH { get; set; }
    }

    public class ServerCommon
    {
        public string ServerAddr { get; set; }


        public int ServerPort { get; set; }
    }
}
