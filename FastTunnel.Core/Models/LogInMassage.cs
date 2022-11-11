// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Collections.Generic;

namespace FastTunnel.Core.Models.Massage
{
    public class LogInMassage : TunnelMassage
    {
        /// <summary>
        /// web穿透隧道列表
        /// </summary>
        public IEnumerable<WebConfig> Webs { get; set; }

        /// <summary>
        /// 端口转发隧道列表
        /// </summary>
        public IEnumerable<ForwardConfig> Forwards { get; set; }
    }
}
