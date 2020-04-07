using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers
{
    public interface IConfigHandler
    {
        LogInRequest GetConfig(JObject content);
    }
}
