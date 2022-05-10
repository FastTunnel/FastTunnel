// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;

namespace FastTunnel.Core.Utilitys;

/// <summary>
/// Assembly工具集
/// </summary>
public static class AssemblyUtility
{
    /// <summary>
    /// 获取版本号
    /// </summary>
    /// <returns></returns>
    public static Version GetVersion()
    {
        return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    }
}
