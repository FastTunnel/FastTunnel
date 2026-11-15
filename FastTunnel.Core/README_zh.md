<div align="center">

<img src="images/logo.png" width="150" align=center />

## FastTunnel-内网穿透

[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![Build](https://github.com/FastTunnel/FastTunnel/actions/workflows/ci.yml/badge.svg?branch=v3)](https://github.com/FastTunnel/FastTunnel/actions/workflows/ci.yml)
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
GitHub : [FastTunnel](https://github.com/FastTunnel/FastTunnel)  
Gitee: [FastTunnel](https://gitee.com/Hgui/FastTunnel)

**有帮助的朋友点下右上角项目的⭐Star支持一下本项目，有需求和bug请提交issue，同时欢迎各位coder进行PR**
### 获得GVP称号

![img1](images/gvp.png)

## FastTunnel有哪些特性
- [x] 在任何地方远程内网计算机（Windows / Linux / macOS）
- [x] 通过子域名或自定义域名访问内网 Web 服务（常用于微信开发、对外预览）
- [x] 端口转发 / 端口映射，访问内网任意 TCP / UDP 服务（MySQL、Redis、RDP 等）
- [x] 一个客户端可绑定多个域名访问内网服务
- [x] 代理组负载均衡：同组名 + 同口令 + 同域名的注册自动成组，按请求随机分发，
      连续失败的成员被动摘除并自动恢复
- [x] 自定义域名校验：拒绝非法域名、平台子域抢注和覆盖他人映射
- [x] 服务端自定义域名黑名单（`FastTunnel:WebDomainBlacklist`）
- [x] TCP/UDP 端口白名单（`FastTunnel:AllowPorts`，单端口或区间）与每客户端端口数上限
      （`FastTunnel:MaxPortsPerClient`）；白名单外端口拒绝登记
- [x] 单端口多路复用（MUX）：转发复用一条长连接，不再逐条回连
- [x] 客户端通道 TLS 加密（默认开启），支持证书 SHA-256 指纹校验
- [x] 控制通道心跳与假死检测；结构化控制协议兼容旧版客户端
- [x] MUX 数据面支持 TCP / WebSocket 承载，可穿过只放行 HTTP(S) 的防火墙
- [x] IPv4 / IPv6 双栈：服务监听、端口转发、内网目标地址
- [x] 客户端身份校验（Token）；命中的凭据绑定到会话，便于审计与封禁定位主体
- [x] 访问来源 IP 白名单（`FastTunnel:WebAllowAccessIps`，支持 IP / CIDR）
- [x] 真实访客 IP 还原：仅可信代理（`FastTunnel:TrustedProxyIps`）的转发头才被采信
- [x] 违规熔断：命中黑名单立即返回 451、撤销映射、断开会话并保存审计记录
- [x] 处置接口（`api/Abuse/*`）：封禁 / 解封 / 人工复核 / 匿名举报；自动系统不能直接永封
- [x] 一户一 token（`FastTunnel:Clients`）：独立主体、域名授权、单会话强制；按 account 熔断不误伤他人
- [x] 运维最小集：`/healthz`、`/metrics`（Prometheus 文本）、`api/Ops/Traffic` 按代理流量明细
- [x] 按代理带宽统计与令牌桶限速（`FastTunnel:Bandwidth`），覆盖四条数据面
- [x] 多格式配置文件：服务端支持 `config/` 下的 `appsettings.json` / `appsettings.ini`，
      客户端支持程序目录下的同名变体；后加载覆盖先加载，全部支持热重载
- [ ] 内容检测（旁路送审、主动巡检）：需先完成用户协议授权，尚未实施
- [ ] P2P 穿透（STCP / XTCP）
- [ ] Dashboard / Admin UI（指标已有 `/metrics`、健康检查已有 `/healthz`）

> 两个「白名单」别混淆：旧文档打勾的「域名白名单限制」从未实现，那个
> `WebAllowAccessIps` 没有任何读取点，v3.3.0 曾删除；**现已重新接线**，成为真正生效的
> 访问来源 IP 白名单。`WebDomainBlacklist` 则是拒绝登记某些自定义域名，作用完全不同。
>
> 使用反向代理时务必同步配置 `TrustedProxyIps`，只填自己的代理回源 IP / CIDR：不配则
> 转发头一律不采信（按 TCP 对端 IP 判定），配成 `0.0.0.0/0` 等于把白名单交给访客自己填。

> **一户一 token**：在服务端 `FastTunnel:Clients` 为每个用户配一条 `{ Id, Token }`，
> 客户端 `FastTunnel:Token` 填自己那条。可选 `SubDomain` / `AllowedHosts` 限定可登记的域名，
> `EnforceSingleSession`（默认 true）让同一主体新登录顶掉旧会话。旧版共享 `Tokens` 仍可用，
> 但会被标成 `legacy:*`，按 token 封禁会误伤所有共用者，也不参与单会话强制。
> 配置了 `Clients` 段就强制校验 token，即使条目全被 `Enabled: false` 停用也不会退回无鉴权。
>
> **运维端点**（用服务端自身地址 + `BinPort` 访问，别用已映射的业务域名）：
> `GET /healthz` 返回 JSON 概览与配置告警；`GET /metrics` 返回 Prometheus 文本；
> `GET /api/Ops/Traffic` 需 JWT，返回按代理的字节数 / 连接数 / 限速等待。
> `FastTunnel:Bandwidth` 配速率上限（字节/秒），优先级为
> `PerProxy` > `PerClient` > `Clients[].BandwidthLimit` > `DefaultBytesPerSecond`，0 表示不限速。
> 生产环境建议网关层限制 `/healthz`、`/metrics` 的来源 IP，或设 `Ops:PublicEndpoints: false`。

> **多格式配置文件**：服务端按 `json → ini` 的顺序加载 `config/` 下所有实际存在的
> `appsettings.{ext}`（后加载覆盖先加载），随后追加环境变体
> `appsettings.{Environment}.{ext}`；客户端同规则，JSON 由默认管线加载、其余格式自动补齐。
> 全部支持热重载。INI 没有原生数组，需要列表时用节名下标（`[FastTunnel:Clients:0]`），
> 复杂结构建议留在 JSON、INI 只做少量覆盖。示例见
> `FastTunnel.Server/config/appsettings.sample.ini` 与 `FastTunnel.Client/appsettings.sample.ini`。

## 立即体验内网穿透
官网：https://suidao.io  

此网站基于本框架开发的穿透平台，用于分享测试使用，如果你需要低频使用内网穿透可以直接注册使用，免去自己搭建和维护的成本；但生产项目请勿使用此服务。

## 快速搭建服务
1. 在 [releases](https://github.com/FastTunnel/FastTunnel/releases) 页面下载对应的程序
2. 根据自己的需求修改客户端以及服务端配置文件`appsettings.json`
3. 服务端运行FastTunnel.Server
4. 客户端运行FastTunnel.Cient

## 使用Docker安装
配置文件和日志文件通过volume挂载，如果之前运行过本镜像，docker可能不会更新至最新的镜像，请手动删除已存在的镜像，然后执行以下命令

```
docker run --detach \
  --publish 1270:1270 \
  --name FastTunnel \
  --restart always \
  --volume /var/FastTunnel/config:/app/config \
  --volume /var/FastTunnel/Logs:/app/Logs \
  springhgui/fasttunnel:latest
```

> 服务端只监听 `FastTunnel:BinPort`（默认 1270）。`EnableForward` 默认 `false`：裸 TCP/UDP
> 转发不经过 HTTP 解析层，既拿不到真实访客来源、也无法做内容审计，是不可审计出口。确需使用时
> 显式改为 `true`，并为每个 `RemotePort` 加 `--publish <port>:<port>`；旧文档里的 1271 已不再使用。

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

### 2. 客户端配置（v3）

v3 的配置根节点是 `FastTunnel`；v2 的 `ClientSettings` / `SSH` 结构已废弃。以「远程内网 Linux + 远程桌面 Windows」为例：
appsettings.json
```
{
  "FastTunnel": {
    "Server": { "ServerAddr": "x.x.x.x", "ServerPort": 1270, "Protocol": "wss" },
    "Token": "TOKEN_FOR_CLIENT_AUTHENTICATION",
    "EnableMux": true,
    "MuxTransport": "tcp",
    "Webs": [
      {
        "LocalIp": "127.0.0.1",
        "LocalPort": 8090,
        "SubDomain": "test",
        "WWW": [ "www.abc.com" ]
      }
    ],
    "Forwards": [
      { "LocalIp": "192.168.0.100", "LocalPort": 22, "RemotePort": 12701, "Protocol": "TCP" },
      { "LocalIp": "192.168.0.101", "LocalPort": 3389, "RemotePort": 12702, "Protocol": "TCP" }
    ]
  }
}
```
> `WWW` 自定义域名需先解析到服务端，且不能是服务端 `WebDomain` 的子域；服务端会校验域名格式，
> 并拒绝覆盖其它客户端已登记的映射。DNS 所有权验证（TXT 挑战）与 ICP 备案校验尚未实现，
> 需自行在接入流程中确认。端口转发默认关闭，需服务端显式设 `EnableForward: true`，
> 并为每个 `RemotePort` 放行防火墙 / 容器端口映射。
>
> 服务端 `appsettings.json` 另有两个访问控制项：`WebAllowAccessIps`（访客来源 IP / CIDR 白名单，
> 为空不限制）与 `TrustedProxyIps`（可信反代回源 IP / CIDR）。判定用的是还原后的真实访客 IP，
> 不是访客可伪造的 `X-Forwarded-For`。外挂 nginx 做 TLS 终止时，nginx 需透传 `Host` 与
> `X-Forwarded-For`，并把回源地址填进 `TrustedProxyIps`；配置示例见
> `docs/content-compliance-plan-v0.1.md` 附录 A。

#### v2 旧版客户端配置（已废弃，仅供迁移参考）

以下内容取自 v2 README，供从 v2 升级的用户对照。v2 的配置根节点是 `ClientSettings`，
`SSH` 数组只能做 TCP 转发；v3 请使用上文的 `FastTunnel` 结构。

```json
{
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
- 打开cmd输入指令 `mstsc`，打开远程对话框，在对话框的计算机输入框，输入 `x.x.x.x:12702` 然后指定用户名密码即可远程内网的windows主机  
![img1](images/remote.png)
  
## 开发/PR
- 安装 .NET 10 SDK 与 Visual Studio 2022 / VS Code（v2 文档里的 .NET 5 / VS2019 已不适用）
- hosts 文件添加 `test.test.cc 127.0.0.1`（服务端默认 `WebDomain=test.cc`）
- `dotnet run --project FastTunnel.Server` 启动服务端
- `dotnet run --project FastTunnel.Client` 启动客户端
- `dotnet test FastTunnel.Core.Tests` 跑单元测试

## 感谢以下贡献者
<a href = "https://github.com/FastTunnel/FastTunnel/graphs/contributors">
  <img src = "https://contrib.rocks/image?repo=FastTunnel/FastTunnel"/>
</a>  

## QQ群交流群

群1：798672272  
群2：935214348  
群3：768089177  

## License
Apache License 2.0

## 特别声明

本项目已加入 [dotNET China](https://gitee.com/dotnetchina)  组织。<br/>

![dotnetchina](https://images.gitee.com/uploads/images/2021/0324/120117_2da9922c_416720.png "132645_21007ea0_974299.png")
