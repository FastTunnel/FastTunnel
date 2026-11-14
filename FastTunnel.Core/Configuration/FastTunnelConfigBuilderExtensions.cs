// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Ini;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace FastTunnel.Core.Configuration;

/// <summary>
/// 多格式配置文件加载入口。支持 JSON 与 INI，
/// 按 <c>json → ini</c> 的顺序加载 <paramref name="configDirectory"/>
/// 下所有实际存在的 <c>appsettings.{ext}</c>（后加载覆盖先加载），随后同样方式追加
/// <c>appsettings.{env}.{ext}</c>（环境变体覆盖基础配置）。所有文件 reloadOnChange。
///
/// 关键约束：
/// - 文件存在性通过 builder 的 IFileProvider 判定（内容根感知），不能用
///   <see cref="File.Exists"/>：Windows 服务下进程 CWD 是 System32，相对路径会判错。
/// - 只要 <paramref name="includeJson"/> 为 true 且一个基础配置都没有，直接抛异常，
///   保持旧版 <c>appsettings.json optional:false</c> 的启动语义。
/// - INI 没有原生数组；需要数组时用节名下标（<c>[FastTunnel:Clients:0]</c>）。
/// </summary>
public static class FastTunnelConfigBuilderExtensions
{
    /// <summary>受支持的扩展名及加载顺序（先加载的可以被后加载的覆盖）。</summary>
    private static readonly string[] OrderedExtensions =
    {
        ".json", ".ini",
    };

    public static IReadOnlyList<string> AddFastTunnelConfigFiles(
        this IConfigurationBuilder builder,
        string configDirectory,
        string? environmentName = null,
        bool includeJson = true)
    {
        var fileProvider = builder.GetFileProvider();
        var loaded = new List<string>();

        void TryAdd(string fileName)
        {
            var path = Combine(configDirectory, fileName);
            if (fileProvider.GetFileInfo(path) is { Exists: true })
            {
                builder.AddSourceFor(path);
                loaded.Add(path);
            }
        }

        foreach (var ext in OrderedExtensions)
        {
            if (ext == ".json" && !includeJson)
            {
                // 客户端等场景已由 Host.CreateDefaultBuilder 加载 JSON，避免重复注册
                continue;
            }

            TryAdd($"appsettings{ext}");
        }

        if (includeJson && loaded.Count == 0)
        {
            var searched = string.Join(", ", OrderedExtensions.Select(e => Combine(configDirectory, $"appsettings{e}")));
            throw new FileNotFoundException("未找到任何配置文件（appsettings.json / appsettings.ini），请至少提供一份", searched);
        }

        if (!string.IsNullOrEmpty(environmentName))
        {
            foreach (var ext in OrderedExtensions)
            {
                if (ext == ".json" && !includeJson)
                {
                    continue;
                }

                TryAdd($"appsettings.{environmentName}{ext}");
            }
        }

        return loaded;
    }

    private static void AddSourceFor(this IConfigurationBuilder builder, string path)
    {
        var ext = Path.GetExtension(path);
        FileConfigurationSource source = ext switch
        {
            ".json" => new JsonConfigurationSource { Path = path },
            ".ini" => new IniConfigurationSource { Path = path },
            _ => throw new InvalidOperationException($"不支持的配置文件扩展名：{path}"),
        };

        source.Optional = false;
        source.ReloadOnChange = true;
        builder.Add(source);
    }

    private static string Combine(string configDirectory, string fileName)
    {
        return string.IsNullOrEmpty(configDirectory)
            ? fileName
            : Path.Combine(configDirectory, fileName).Replace('\\', '/');
    }
}
