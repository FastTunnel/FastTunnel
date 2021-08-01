using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Config
{
    public interface IClientConfig
    {
        public SuiDaoServer Server { get; set; }

        public IEnumerable<WebConfig> Webs { get; set; }

        public IEnumerable<ForwardConfig> Forwards { get; set; }
    }

    public class SuiDaoServer
    {
        public string ServerAddr { get; set; }

        public int ServerPort { get; set; }
    }
}
