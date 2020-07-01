using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SuiDao.Client.Models
{
    public class LogInByKeyMassage : TunnelMassage
    {
        public string key { get; set; }

        public long server_id { get; set; }
    }
}
