<p align="center"><img src="images/logo.png" width="150" align=center /></p>

## FastTunnel -内网穿透利器
[README](README.md) | [中文文档](README_zh.md)

***
演示地址 https://suidao.io  

演示程序源码：[FastTunnel.SuiDao](https://github.com/SpringHgui/FastTunnel.SuiDao)

**有帮助的朋友给个:star:支持一下，有需求和bug请提交issue，同时欢迎各位PR:blush::blush::blush:**

***

[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg?style=flat-square)](https://github.com/SpringHgui/frp/pulls)
[![Build status](https://github.com/anjoy8/blog.core/workflows/.NET%20Core/badge.svg)](https://github.com/SpringHgui/FastTunnel/actions)
[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![CircleCI](https://circleci.com/gh/gothinkster/aspnetcore-realworld-example-app.svg?style=svg)](https://circleci.com/gh/SpringHgui/FastTunnel)
## What is FastTunnel？
FastTunnel是一款高性能跨平台内网穿透工具，使用它可以实现在公网上访问您的内网服务。

## What can FastTunel do？
- [x] 远程内网计算机 Windows/Linux/Mac
- [x] 用自定义域名访问内网web服务（常用于微信开发）
- [x] 端口转发/端口映射，访问内网任意端口提供的服务 mysql、redis、ftp等等
- [ ] Support any other domain names overlay current binding
- [ ] p2p穿透


## Quickstart
1. 在 [releases](https://github.com/SpringHgui/FastTunnel/releases) 页面下载对应的程序
2. 根据自己的需求修改配置文件`appsettings.json`
3. 服务端运行FastTunnel.Server
4. 客户端运行FastTunnel.Cient

## Run on Linux/Mac os？
#### Windows
直接双击 `FastTunnel.Client.exe` Ready to run
#### Linux
`chmod +x FastTunnel.Client`  
`./FastTunnel.Client`
#### Mac
直接运行 `FastTunnel.Client`

## 配置示例
### 1. 用自定义域名访问内网web服务
- 例如你拥有一个服务器，公网ip地址为 `110.110.110.110` ,同时你有一个顶级域名为 `abc.com` 的域名，你希望访问 `test.abc.com`可以访问内网的一个网站。
- 你需要新增一个域名地址的DNS解析，类型为`A`，名称为 `*` , ipv4地址为 `110.110.110.110` ,这样 `*.abc.com`的域名均会指向`110.110.110.110`的服务器，由于`FastTunnel`默认监听的http端口为1270，所以要访问`http://test.abc.com:1270`
- #### 如果不希望每次访问都带上端口号，可以通过`nginx`转发实现。
```
http {
    # 添加resolver 
    resolver 8.8.8.8;

    # 设置 *.abc.com 转发至1270端口
    server {
      server_name  *.abc.com;
      location / {
         proxy_pass http://$host:1270;
         proxy_set_header   Host             $host;
         proxy_set_header   X-Real-IP        $remote_addr;
         proxy_set_header   X-Forwarded-For  $proxy_add_x_forwarded_for;
      }

      # 可选
      error_log /var/log/nginx/error_ft.log error;
    }
}
```

- 如果服务端配置的域名为`ft.suidao.io`, 则通过子域名`test.ft.suidao.io:1270`访问在本地的站点，IIS配置如下：
![img1](images/iis-web.png)

### 2. 远程内网计算机 Windows/Linux/Mac

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
#### ssh远程内网linux主机 (ip:192.168.0.100)

假设内网主机的用户名为: root，服务器ip为x.x.x.x，访问内网的两个主机分别如下
```
ssh -oPort=12701 root@x.x.x.x
```

#### mstsc远程桌面Windows主机(ip:192.168.0.101)
#### 被控制端设置
- 打开cmd输入指令 `sysdm.cpl` 在弹出的对话框中选中允许远程连接此计算机  
![img1](images/setallow.png)
#### 控制端设置
- 打开cmd输入指令 `mstsc`，打开远程对话框，在对话框的计算机输入框，输入 `x.x.x.x:12701` 然后指定用户名密码即可远程内网的windows主机  
![img1](images/remote.png)
  
    
## 开发/PR
- 安装 `vs2019`
- 安装 `dotnetcore runtime&sdk 3.1` 或以上版本

## License
Apache License 2.0
