# FastTunnel

- FastTunnel是一款内网络代理工具，能够快速进行内网穿透。其核心原理是通过http代理，所以使用的前提条件是
1. 必须有一台公网的服务器。
2. 拥有自己的域名。
- 使用 .net core编写，可运行于windows、mac、linux等平台。

## 已实现功能

- 通过自定义域名访问部署于内网的 web 服务

# 快速使用

## 配置服务端和客户端程序
1. 分别在服务端和客户端PC上安装[.net core runtime]([url](https://dotnet.microsoft.com/download?missing_runtime=true)) 运行时（`v3.1`及以上版本），根据不同操作系统选择对应的运行时安装程序。
2. 在命令行输入 `dotnet -v` 检查运行时安装是否成功。
3. 在 [release]([url](https://github.com/SpringHgui/FastTunnel/releases)) 页面下载编译好的`zip`程序包，解压到任意目录。
4. 分别修改客户端和服务端的配置文件 `appsettings.json`。
5. 在程序根目录下执行 `dotnet FastTunnel.Client.dll`(客户端)，`dotnet FastTunnel.Server.dll`（服务端）。

## 通过自定义域名配置访问
- 例如你拥有一个服务器，公网ip地址为 `110.110.110.110` ,同时你有一个顶级域名为 `test.cc` 的域名，你希望访问 `test.test.cc`可以访问内网的一个网站。
- 你需要新增一个域名地址的DNS解析，类型为`A`，名称为 `*` , ipv4地址为 `110.110.110.110` ,这样 `*.test.cc`的域名均会指向`110.110.110.110`的服务器，由于`FastTunnel`默认监听的http端口为1270，所以要访问`http://test.test.cc:1270`
- 如果不希望每次访问都带上端口号，可以通过`nginx`转发实现。

# 开发
- 安装 `vs2019`
- 安装 `dotnetcore runtime&sdk 3.1` 或以上版本

# License
Apache License 2.0