// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Runtime.InteropServices;

namespace FastTunnel.LoadRunner;

internal static class NativeMethods
{
    /// <summary>
    /// 提高本进程的定时器精度到 1ms，让定速用的 <see cref="System.Threading.Thread.Sleep(int)"/>
    /// 不再有 ~15.6ms 的粒度误差（Windows 10 2004 起该设置仅影响调用进程）。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint period);
}
