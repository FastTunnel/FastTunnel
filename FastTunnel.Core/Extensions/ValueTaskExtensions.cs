// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Extensions
{
    internal static class ValueTaskExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Task GetAsTask(this in ValueTask<FlushResult> valueTask)
        {
            // Try to avoid the allocation from AsTask
            if (valueTask.IsCompletedSuccessfully)
            {
                // Signal consumption to the IValueTaskSource
                valueTask.GetAwaiter().GetResult();
                return Task.CompletedTask;
            }
            else
            {
                return valueTask.AsTask();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTask GetAsValueTask(this in ValueTask<FlushResult> valueTask)
        {
            // Try to avoid the allocation from AsTask
            if (valueTask.IsCompletedSuccessfully)
            {
                // Signal consumption to the IValueTaskSource
                valueTask.GetAwaiter().GetResult();
                return default;
            }
            else
            {
                return new ValueTask(valueTask.AsTask());
            }
        }
    }
}
