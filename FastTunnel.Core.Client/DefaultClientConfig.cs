// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System.Collections.Generic;

namespace FastTunnel.Core.Config
{
    public class DefaultClientConfig : IClientConfig
    {
        public SuiDaoServer Server { get; set; }

        public string Token { get; set; }

        public IEnumerable<WebConfig> Webs { get; set; }

        public IEnumerable<ForwardConfig> Forwards { get; set; }
    }
}
