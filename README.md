<p align="center"><img src="images/logo.png" width="150" align=center /></p>

# FastTunnel
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg?style=flat-square)](https://github.com/SpringHgui/frp/pulls)
[![Build status](https://github.com/anjoy8/blog.core/workflows/.NET%20Core/badge.svg)](https://github.com/SpringHgui/FastTunnel/actions)
[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![CircleCI](https://circleci.com/gh/gothinkster/aspnetcore-realworld-example-app.svg?style=svg)](https://circleci.com/gh/SpringHgui/FastTunnel)
- FastTunnel是一款跨平台网络代理工具，能够快速进行内网穿透。既然是代理，所以使用的时应具备
1. 拥有一台公网的服务器
2. 拥有自己的域名（如果使用域名穿透访问web则需要）  
  
***如果上面两个都没有，您还可以使用下面的测试服务器。***

## 特性
- [x] 用自定义域名访问内网web服务（常用于微信开发）
- [x] 远程内网计算机 Windows/Linux/Mac
- [ ] 点对点p2p穿透

## 测试服务器 （请勿滥用）
```
ip `45.132.12.57`
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
## 快速使用
1. 在 [releases](https://github.com/SpringHgui/FastTunnel/releases) 页面下载对应的程序
2. 分别修改配置文件`appsettings.json`
3. 服务端运行FastTunnel.Server.exe(windows)，其他平台安装dotnetcore运行时，执行 dotnet FastTunnel.Server.dll
4. 客户端运行FastTunnel.Cient.exe(windows)，其他平台同安装dotnetcore运行时，执行 dotnet FastTunnel.Client.dll

## 1. 用自定义域名访问内网web服务
- 例如你拥有一个服务器，公网ip地址为 `110.110.110.110` ,同时你有一个顶级域名为 `test.cc` 的域名，你希望访问 `test.test.cc`可以访问内网的一个网站。
- 你需要新增一个域名地址的DNS解析，类型为`A`，名称为 `*` , ipv4地址为 `110.110.110.110` ,这样 `*.test.cc`的域名均会指向`110.110.110.110`的服务器，由于`FastTunnel`默认监听的http端口为1270，所以要访问`http://test.test.cc:1270`
- 如果不希望每次访问都带上端口号，可以通过`nginx`转发实现。
- 如果服务端配置的域名为`ft.suidao.io`, 则通过子域名`test.ft.suidao.io`访问在本地的站点，IIS配置如下：
![img1](images/iis-web.png)

## 2. 远程内网计算机 Windows/Linux/Mac

客户端配置如下，内网有两台主机，ip如下:
appsettings.json
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

# License
Apache License 2.0

# 联系作者
hangui0127@qq.com
