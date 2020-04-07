using FastTunnel.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Handlers
{
    public class LoginHandler : ILoginHandler
    {
        public LogInRequest GetConfig(object content)
        {
            return (content as JObject).ToObject<LogInRequest>();
        }
    }
}
