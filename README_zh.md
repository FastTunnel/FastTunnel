<div align="center">

<img src="images/logo.png" width="150" align=center />

## FastTunnel-内网穿透

[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![Build status](https://github.com/anjoy8/blog.core/workflows/.NET%20Core/badge.svg)](https://github.com/SpringHgui/FastTunnel/actions)
[![Nuget](https://img.shields.io/nuget/v/FastTunnel.Core)](https://www.nuget.org/packages/FastTunnel.Core/)
[![Nuget](https://img.shields.io/nuget/dt/FastTunnel.Core)](https://www.nuget.org/packages/FastTunnel.Core/)

[README](README.md) | [中文文档](README_zh.md)
    
**本项目支持任何商用和二次开发行为，但对于剽窃复制本项目代码、实现方案或架构重新开源包装成自己的开源作品，严重鄙视。**

</div>

***

## FastTunnel是什么？
- FastTunnel是一款高性能跨平台内网穿透工具，使用它可以实现将内网服务暴露到公网供自己或任何人访问。
- 与其他穿透工具不同的是，FastTunnel项目致力于打造一个易于扩展、易于维护的内网穿透框架。
- 你可以通过引用`FastTunnel.Core`的nuget包构建出自己的穿透应用，并针自己所需的业务扩展功能。

### 开源仓库地址
GitHub : [FastTunnel](https://github.com/SpringHgui/FastTunnel)  
Gitee: [FastTunnel](https://gitee.com/Hgui/FastTunnel)

**有帮助的朋友点下右上角项目的⭐Star支持一下本项目，有需求和bug请提交issue，同时欢迎各位coder进行PR**
### 获得GVP称号

![img1](images/gvp.png)

## FastTunnel有哪些特性
- [x] 在任何地方远程内网计算机(公司或家里的计算机) Windows/Linux/Mac
- [x] 通过自定义域名访问内网web服务（常用于微信开发或对外开放web服务）
- [x] 端口转发/端口映射，访问内网任意端口提供的服务 mysql、redis、ftp等等
- [ ] p2p穿透
- [x] 支持绑定多个域名访问内网服务
- [x] 支持域名白名单限制
- [x] 支持客户端身份校验

## 立即体验内网穿透
官网：https://suidao.io  

此网站基于本框架开发的穿透平台，用于分享测试使用，如果你需要低频使用内网穿透可以直接注册使用，免去自己搭建和维护的成本；但生产项目请勿使用此服务。

## 快速搭建服务
1. 在 [releases](https://github.com/SpringHgui/FastTunnel/releases) 页面下载对应的程序
2. 根据自己的需求修改客户端以及服务端配置文件`appsettings.json`
3. 服务端运行FastTunnel.Server
4. 客户端运行FastTunnel.Cient

## 使用Docker安装
配置文件和日志文件通过volume挂载，如果之前运行过本镜像，docker可能不会更新至最新的镜像，请手动删除已存在的镜像，然后执行以下命令

```
docker run --detach \
  --publish 1270:1270 --publish 1271:1271 \
  --name FastTunnel \
  --restart always \
  --volume /var/FastTunnel/config:/app/config \
  --volume /var/FastTunnel/Logs:/app/Logs \
  springhgui/fasttunnel:latest
```

## 如何在 Linux/Mac系统运行？
#### Windows
直接双击 `FastTunnel.Client.exe` 即可运行
#### Linux
`chmod +x FastTunnel.Client`  
`./FastTunnel.Client`
#### Mac
直接运行 `FastTunnel.Client`

## 相关高质量博客

[原理和教程](/docs/blogs.md)  

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

假设内网主机的用户名为 root，服务器ip为x.x.x.x，访问内网的两个主机分别如下
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
- 安装 `vs2019` 最新版本
- 安装 `.Net5` 或以上版本 `https://dotnet.microsoft.com/download/dotnet/5.0`
- host文件中添加 `test.test.cc 127.0.0.1`等
- 启动 fasttunnel.server
- 启动 fasttunnel.client
- 
## 感谢以下贡献者
<a href = "https://github.com/FastTunnel/FastTunnel/graphs/contributors">
  <img src = "https://contrib.rocks/image?repo=FastTunnel/FastTunnel"/>
</a>  

## QQ群交流群

<div align="center"><img src="images/qqgroup.png" width="150" align=center /></div>

## License
Apache License 2.0

## 特别声明

本项目已加入 [dotNET China](https://gitee.com/dotnetchina)  组织。<br/>

![dotnetchina](https://images.gitee.com/uploads/images/2021/0324/120117_2da9922c_416720.png "132645_21007ea0_974299.png")
