using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers.Server
{
    public class ConfigHandler : IConfigHandler
    {
        public LogInMassage GetConfig(JObject content)
        {
            return content.ToObject<LogInMassage>();
        }
    }
}
