using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfig : IServerConfig
    {
        public string WebDomain { get; set; }

        public string[] WebAllowAccessIps { get; set; }

        public bool EnableForward { get; set; } = false;

        [Obsolete("由Tokens替换")]
        public string Token { get; set; }

        public List<string> Tokens { get; set; }

        public ApiOptions Api { get; set; }

        public class ApiOptions
        {
            public JWTOptions JWT { get; set; }

            public Account[] Accounts { get; set; }
        }

        public class JWTOptions
        {
            public int ClockSkew { get; set; }

            public string ValidAudience { get; set; }

            public string ValidIssuer { get; set; }

            public string IssuerSigningKey { get; set; }

            public int Expires { get; set; }
        }

        public class Account
        {
            public string Name { get; set; }

            public string Password { get; set; }
        }
    }
}
