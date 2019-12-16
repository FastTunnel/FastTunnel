using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class LogInRequest
    {
        public IEnumerable<WebConfig> WebList { get; set; }
    }
}
