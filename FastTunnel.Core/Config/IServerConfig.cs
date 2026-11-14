// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;

namespace FastTunnel.Core.Config;

public interface IServerConfig
{
    string WebDomain { get; set; }

    string[] WebDomainBlacklist { get; set; }

    string[] WebAllowAccessIps { get; set; }

    string[] TrustedProxyIps { get; set; }

    bool EnableForward { get; set; }

    List<AllowPortOptions> AllowPorts { get; set; }

    int MaxPortsPerClient { get; set; }

    List<ClientCredentialOptions> Clients { get; set; }

    bool EnforceSingleSession { get; set; }

    BandwidthOptions Bandwidth { get; set; }

    OpsOptions Ops { get; set; }
}
