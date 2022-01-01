// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Extensions
{
    public static class TaskCompletionSourceExtensions
    {
        public static void SetTimeOut<T>(this TaskCompletionSource<T> tcs, int timeoutMs, Action? action)
        {
            var ct = new CancellationTokenSource(timeoutMs);
            ct.Token.Register(() =>
            {
                if (tcs.Task.IsCompleted)
                    return;

                tcs.TrySetCanceled();
                action?.Invoke();
            }, useSynchronizationContext: false);
        }
    }
}
