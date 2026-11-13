// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Client;

public class LogHandler : IClientHandler
{
    private readonly ILogger<LogHandler> _logger;

    public LogHandler(ILogger<LogHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken)
    {
        var text = msg;

        // 新版为 {"text":"...","level":"..."}；旧服务端下发的是裸文本，直接透传。
        if (ControlProtocol.IsStructured(msg))
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<LogMessage>(msg, ControlProtocol.JsonOptions);
                if (payload != null && !string.IsNullOrEmpty(payload.Text))
                {
                    text = payload.Text;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 结构异常时按原文输出，不影响主流程
            }
        }

        _logger.LogInformation(text.Replace("\n", string.Empty));
        await Task.CompletedTask;
    }
}
