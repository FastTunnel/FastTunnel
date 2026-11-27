// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

#pragma warning disable CS1591

namespace FastTunnel.Api.Models
{
    /// <summary>兑换入网码（匿名）。</summary>
    public class PeerEnrollRequest
    {
        public string Code { get; set; } = string.Empty;

        /// <summary>客户端主机名，用作设备名与主体编号的基础。</summary>
        public string Hostname { get; set; } = string.Empty;
    }

    /// <summary>签加入网码。</summary>
    public class PeerTicketRequest
    {
        public string Name { get; set; } = string.Empty;

        public int? Minutes { get; set; }
    }

    /// <summary>按主体编号操作设备。</summary>
    public class PeerSubjectRequest
    {
        public string Id { get; set; } = string.Empty;

        public string? Reason { get; set; }
    }
}
