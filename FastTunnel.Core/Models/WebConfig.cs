// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class WebConfig
    {
        /// <summary>
        /// 子域名
        /// </summary>
        public string SubDomain { get; set; }

        /// <summary>
        /// 本地IP
        /// </summary>
        public string LocalIp { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public int LocalPort { get; set; }

        /// <summary>
        /// 个人域名
        /// </summary>
        public string[] WWW { get; set; }
    }
}
