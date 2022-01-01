// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfig : IServerConfig
    {
        public string WebDomain { get; set; }

        public string[] WebAllowAccessIps { get; set; }

        public bool EnableForward { get; set; }

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
