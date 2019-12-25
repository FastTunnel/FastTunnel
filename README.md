# FastTunnel
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg?style=flat-square)](https://github.com/SpringHgui/frp/pulls)
[![Build status](https://github.com/anjoy8/blog.core/workflows/.NET%20Core/badge.svg)](https://github.com/SpringHgui/FastTunnel/actions)
[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![CircleCI](https://circleci.com/gh/gothinkster/aspnetcore-realworld-example-app.svg?style=svg)](https://circleci.com/gh/SpringHgui/FastTunnel)
- FastTunnel是一款跨平台网络代理工具，能够快速进行内网穿透。既然是代理，所以使用的时应具备
1. 拥有一台公网的服务器
2. 拥有自己的域名（如果使用域名穿透访问web则需要）  
  
***如果上面两个都没有，您还可以使用下面的测试服务器。***

## 功能特色
1. 用自定义域名访问内网web服务（常用于微信开发）
2. 远程内网计算机 Windows/Linux/Mac
# 快速开始

## 测试服务器 （请勿滥用）
```
ip `154.202.58.219`
```
```
已开端口号，括号内容为本端口的测试用途  
1270(httpProxy) 1271(bindPort) 1273(ssh) 1274(ssh) 1275(ssh)
```
```
域名解析
A *.ft.suidao.io
```
```
本服务器已运行 `FastTunnel.Server` 本地可以直接运行客户端连接  
nginx反向代理已开启，web穿透可不加端口号1270即可直接访问。
```
## 配置服务端和客户端程序
1. 分别在服务端和客户端PC上安装[.net core runtime]([url](https://dotnet.microsoft.com/download?missing_runtime=true)) 运行时（`v3.1`及以上版本），根据不同操作系统选择对应的运行时安装程序。
2. 在命令行输入 `dotnet -v` 检查运行时安装是否成功。
3. 在 [release]([url](https://github.com/SpringHgui/FastTunnel/releases)) 页面下载编译好的`zip`程序包，解压到任意目录。
4. 分别修改客户端和服务端的配置文件 `appsettings.json`。
5. 在程序根目录下执行 `dotnet FastTunnel.Client.dll`(客户端)，`dotnet FastTunnel.Server.dll`（服务端）。

## 1. 用自定义域名访问内网web服务
- 例如你拥有一个服务器，公网ip地址为 `110.110.110.110` ,同时你有一个顶级域名为 `test.cc` 的域名，你希望访问 `test.test.cc`可以访问内网的一个网站。
- 你需要新增一个域名地址的DNS解析，类型为`A`，名称为 `*` , ipv4地址为 `110.110.110.110` ,这样 `*.test.cc`的域名均会指向`110.110.110.110`的服务器，由于`FastTunnel`默认监听的http端口为1270，所以要访问`http://test.test.cc:1270`
- 如果不希望每次访问都带上端口号，可以通过`nginx`转发实现。

## 2. 远程内网计算机 Windows/Linux/Mac

客户端配置如下，内网有两台主机，ip如下:
```
 "ClientSettings": {
    "Common": {
      "ServerAddr": "xxx.xxx.xxx.xxx",
      "ServerPort": 1271
    },
    "SSH": [
      {
        "LocalIp": "192.168.0.100", // linux主机
        "LocalPort": 22,            // ssh远程默认端口号
        "RemotePort": 12701
      },
      {
        "LocalIp": "192.168.0.101", // windows主机
        "LocalPort": 3389,          // windows远程桌面默认端口号
        "RemotePort": 12702
      }
    ]
  }
```
## ssh远程内网linux主机 (ip:192.168.0.100)

假设内网主机的用户名为 root，服务器ip为x.x.x.x，访问内网的两个主机分别如下
```
ssh -oPort=12701 root@x.x.x.x
```

## mstsc远程桌面Windows主机(ip:192.168.0.101)
### 被控制端设置
- 打开cmd输入指令 `sysdm.cpl` 在弹出的对话框中选中允许远程连接此计算机  
![img1](images/setallow.png)
### 控制端设置
- 打开cmd输入指令 `mstsc`，打开远程对话框，在对话框的计算机输入框，输入 `x.x.x.x:12701` 然后指定用户名密码即可远程内网的windows主机  
![img1](images/remote.png)
# 参与开发/PR
- 安装 `vs2019`
- 安装 `dotnetcore runtime&sdk 3.1` 或以上版本

# Dev Plan
- 客户端心跳

# License
Apache License 2.0
