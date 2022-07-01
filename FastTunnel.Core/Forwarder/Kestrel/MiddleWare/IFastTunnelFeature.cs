// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Models;
using FastTunnel.Core.Protocol;

namespace FastTunnel.Core.Forwarder.Kestrel.MiddleWare;

internal interface IFastTunnelFeature
{
    public WebInfo MatchWeb { get; set; }

    public IList<string> HasReadLInes { get; set; }

    public string Method { get; set; }
    public string Host { get; set; }
    public string MessageId { get; set; }
}
