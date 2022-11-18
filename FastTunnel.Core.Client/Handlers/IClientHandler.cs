// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Config;
using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace FastTunnel.Core.Handlers.Client
{
    public interface IClientHandler
    {
        /// <summary>
        /// 处理消息
        /// </summary>
        /// <param name="cleint"></param>
        /// <param name="msg"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken);
    }

}
