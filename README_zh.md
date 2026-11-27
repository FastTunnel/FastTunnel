<div align="center">

<img src="images/logo.png" width="150" align=center />

## FastTunnel-内网穿透

[![License](https://img.shields.io/badge/license-Apache%202-green.svg)](https://www.apache.org/licenses/LICENSE-2.0)
[![Build](https://github.com/FastTunnel/FastTunnel/actions/workflows/ci.yml/badge.svg?branch=v3)](https://github.com/FastTunnel/FastTunnel/actions/workflows/ci.yml)
[![Nuget](https://img.shields.io/nuget/v/FastTunnel.Core)](https://www.nuget.org/packages/FastTunnel.Core/)
[![Nuget](https://img.shields.io/nuget/dt/FastTunnel.Core)](https://www.nuget.org/packages/FastTunnel.Core/)

[README](README.md) | [中文文档](README_zh.md)

</div>

***

<div align="center">

# 更多开源

## 由于买不起 Navicat，我手搓了一个 Navicat [Daro - navicat 替代品](https://github.com/SpringHgui/daro)

</div>

## FastTunnel
高性能跨平台内网穿透工具，使用它可以实现将内网服务暴露到公网供自己或任何人访问。
- 与其他穿透工具不同的是，FastTunnel项目致力于打造一个易于扩展、易于维护的内网穿透框架。
- 你可以通过引用`FastTunnel.Core`的nuget包构建出自己的穿透应用，并针自己所需的业务扩展功能。

## 文档
[https://docs.suidao.io](https://docs.suidao.io)  

v2 旧版文档（存档）：[快速开始](https://github.com/SpringHgui/opentask-doc/blob/master/docs/fasttunel/getting-started.md)

## 性能对比（frp 0.72.0）

单机 loopback、1KB 静态页、256 并发 keep-alive，两侧关闭 TLS 与日志：

| 指标 | FastTunnel | frp `type=tcp` |
|---|---|---|
| 峰值吞吐 | **≈ 43 000 RPS** | ≈ 34 000 RPS |
| 内存（Server+Client） | 172～217 MB | 230～248 MB |

FastTunnel 与 frp `type=tcp` 在 10 000 RPS 以内都零错误达成；frp 的 `type=http` 模式明显顶不到这个量级。
表中数据为一次本机（16 核）结果，下面的图是 CI 在共享 4 vCPU runner 上的最新结果，两者绝对值不可直接比较。

**HTTP 模式** — FastTunnel Web（vhost）对比 frp `type=http`：

![FastTunnel vs frp — HTTP 模式](https://raw.githubusercontent.com/FastTunnel/fasttunnel-bench/main/results/latest/chart-http.png)

**TCP 模式** — FastTunnel Forwards 对比 frp `type=tcp`：

![FastTunnel vs frp — TCP 模式](https://raw.githubusercontent.com/FastTunnel/fasttunnel-bench/main/results/latest/chart-tcp.png)

测试方法、原始数据与复现脚本见 [FastTunnel/fasttunnel-bench](https://github.com/FastTunnel/fasttunnel-bench)。

**使用内网穿透暴露3389端口的，一定要注意，电脑的密码一定要设置复杂一点，避免被黑客破解计算机登录密码，造成损失。**   
**使用内网穿透暴露3389端口的，一定要注意，电脑的密码一定要设置复杂一点，避免被黑客破解计算机登录密码，造成损失。**   
**使用内网穿透暴露3389端口的，一定要注意，电脑的密码一定要设置复杂一点，避免被黑客破解计算机登录密码，造成损失。**   

## 开源仓库地址
[GitHub](https://github.com/FastTunnel/FastTunnel)  
[Gitee](https://gitee.com/Hgui/FastTunnel)

**有帮助的朋友点下右上角项目的⭐Star支持一下本项目，本项开发进度需要社区的推动，无论是提交issue还是PR，亦或是完善文档；想加入FastTunnel组织请发邮件给我！**

## GVP最有价值开源项目

![img1](images/gvp.png)

## FastTunnel有哪些特性
- [x] 在任何地方远程内网计算机（Windows / Linux / macOS）
- [x] 通过子域名或自定义域名访问内网 Web 服务（常用于微信开发、对外预览）
- [x] 端口转发 / 端口映射，访问内网任意 TCP / UDP 服务（MySQL、Redis、RDP 等）
- [x] 一个客户端可绑定多个域名访问内网服务
- [x] 代理组负载均衡：多个客户端用相同组名 + 组口令 + 相同域名注册后自动成组，
      服务端按请求随机分发给组内存活成员（`WebConfig.Group` / `GroupKey`，
      对齐 frp `loadBalancer.group` 语义）；配合被动摘除
      （`FastTunnel:LoadBalancer:FailureThreshold` / `RecoverySeconds`）实现故障转移
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
      客户端支持程序目录下的同名变体；后加载覆盖先加载，全部支持热重载（示例见
      `FastTunnel.Server/config/appsettings.sample.ini`；INI 无原生数组，列表用节名下标）
- [ ] 内容检测（旁路送审、主动巡检）：需先完成用户协议授权，尚未实施
- [x] 私有 TCP P2P：托管 UDP 直连与 WSS 中继自动选路；`p2p-enroll` 入网码让设备免配置接入；无服务端时可 `p2p-pair` 手动配对；
      提供端可放行端口范围，访客端 SOCKS5 出口用 `服务名.提供端ID.ft:端口` 直连任意已授权端口
- [ ] Dashboard / Admin UI（指标已有 `/metrics`、健康检查已有 `/healthz`）

> 两个「白名单」别混淆：旧文档打勾的「域名白名单限制」从未实现，那个
> `WebAllowAccessIps` 没有任何读取点，v3.3.0 曾删除；**现已重新接线**，成为真正生效的
> 访问来源 IP 白名单。`WebDomainBlacklist` 则是拒绝登记某些自定义域名，作用完全不同。
>
> 使用反向代理时务必同步配置 `TrustedProxyIps`，只填自己的代理回源 IP / CIDR：不配则
> 转发头一律不采信（按 TCP 对端 IP 判定），配成 `0.0.0.0/0` 等于把白名单交给访客自己填。

> 一户一 token（`FastTunnel:Clients`）、带宽限速（`FastTunnel:Bandwidth`）与
> `/healthz`、`/metrics` 的配置说明见 [FastTunnel.Core/README_zh.md](FastTunnel.Core/README_zh.md)。

## 私有 TCP P2P

### 设备入网：服务端维护一次，客户端装到哪都能用

不想每台设备都发一份 `appsettings.json`，就让管理员在节点上签一枚一次性入网码，
设备自己兑换属于自己的长期凭据。入网后的主体与 `Clients` 里手写的条目完全等价。

节点侧开启 `FastTunnel:EnableP2P` 与 `FastTunnel:P2PInviteKey`（`p2p-invite-key` 可生成），
在**服务端工作目录**运行：

```powershell
.\FastTunnel.Server.exe p2p-enroll laptop 30
```

命令打印入网码、服务端证书指纹和对应的客户端命令。设备侧执行：

```powershell
.\FastTunnel.Client.exe p2p-enroll https://node.example.net:443 fte1.xxxx.1780000000.AABB <证书指纹>
```

成功后客户端工作目录出现 `fasttunnel-peer.json`（权限 `0600`），此后直接启动
`FastTunnel.Client` 即可接入，换网络、换机器都不用再改配置。该文件的优先级低于
`appsettings.json`：显式写了 `FastTunnel:Token` 时仍以它为准。入网码默认有效 15 分钟
（`FastTunnel:P2PEnrollment:EnrollMinutes`，最长 60），**只能用一次**，泄露了也已被兑换。

服务端在 `FastTunnel:P2PEnrollment:StateFile`（默认 `config/peers.json`）记录设备台账，
只保存凭据的 SHA-256 指纹，明文 token 全链路只在兑换响应里出现一次。管理接口：

| 接口 | 鉴权 | 作用 |
| --- | --- | --- |
| `POST /api/Peers/Create` | JWT | 签加入网码 |
| `POST /api/Peers/Enroll` | 入网码 | 设备兑换凭据 |
| `GET /api/Peers/List` | JWT | 列出设备、最后上线时间 |
| `POST /api/Peers/Revoke` / `Restore` | JWT | 吊销 / 恢复，吊销后进行中的会话在下一次复检即断开 |
| `POST /api/Peers/Remove` | JWT | 删除记录，需重新入网 |

设备数上限由 `FastTunnel:P2PEnrollment:MaxPeers` 控制（默认 512）。台账是单进程状态文件，
多实例部署需要共享存储，生产环境按**一副本 + 持久卷**挂载 `config/` 运行。

### 无 FastTunnel 服务端时手动配对

两端只需启动新版客户端。以 Windows 远程桌面为例，提供端运行下面第一条命令，访客端运行第二条。
示例使用 Cloudflare 的公共 STUN，也可以填自己的 STUN 地址。

```powershell
.\FastTunnel.Client.exe p2p-pair share 3389 stun:stun.cloudflare.com:3478
.\FastTunnel.Client.exe p2p-pair connect 13389 stun:stun.cloudflare.com:3478
```

1. 提供端将命令打印的一行**邀请**通过可信聊天发送给访客。
2. 访客粘贴邀请并回车，将打印的一行**应答**发回提供端。
3. 提供端粘贴应答并回车。两端显示直连就绪后，访客连接本机 `127.0.0.1:13389`。

邀请有效期为 5 分钟，操作时两端保持客户端运行。手动模式通过 UDP 直连，打洞失败会结束，
没有配置中继。局域网内可省略 STUN 参数，仅探测本机接口地址。

### 用已有协调节点发放短期测试邀请

测试者只需运行 FastTunnel 客户端。节点管理员在服务端目录运行
`FastTunnel.Server p2p-invite-key` 生成签名密钥，将其放入服务端的
`FastTunnel:P2PInviteKey`，并开启 `FastTunnel:EnableP2P`。签名密钥仅保存在协调节点。

以 Windows 远程桌面为例，在节点上运行：

```powershell
.\FastTunnel.Server.exe p2p-invite wss://node.example.net:443 rdp 3389 13389 30
```

命令生成提供端和访客端各自的 `appsettings.json`。把文件分别交给两端，放在各自的客户端工作目录，
再启动客户端。访客在本机连接 `127.0.0.1:13389`，客户端日志会显示实际走 UDP 直连、TCP/TLS 直连或 WSS 中继。
邀请默认有效 30 分钟，可配置为 1–120 分钟，只允许这一对客户端访问指定的 P2P 服务。
协调节点使用自签证书时，在有效分钟数后附上其 SHA-256 指纹。更换签名密钥会使该节点签发的旧邀请失效。

### 手动配置

两端都运行 FastTunnel 客户端。服务端在 `FastTunnel` 下启用 `"EnableP2P": true`，
并在 `Clients` 中给提供端和访客分配独立凭据，例如主体 `home` 和 `laptop`。
两端的 `FastTunnel:Server` 使用 WSS，分别填写各自的 `FastTunnel:Token`。
使用自签证书时，填写服务端启动日志提供的 `CertificateFingerprint`。

在提供端的 `FastTunnel` 下加入一行，将本机 SSH 端口授权给 `laptop`。

```json
"P2P": { "Share": { "ssh": "22@laptop" } }
```

在访客端的 `FastTunnel` 下加入一行，将本机 `2222` 接到 `home` 的 SSH 服务。

```json
"P2P": { "Connect": { "home/ssh": 2222 } }
```

两端的地址默认是 `127.0.0.1`。向多个访客授权时，可填写 `"22@laptop,desktop"`。
需要自定义地址时，可以使用下面的 `Services` 和 `Visitors` 写法。

提供端的 `FastTunnel:P2P` 配置如下。

```json
{
  "Services": [
    { "Name": "ssh", "LocalHost": "127.0.0.1", "LocalPort": 22, "AllowVisitors": ["laptop"] }
  ]
}
```

访客端的 `FastTunnel:P2P` 配置如下。

```json
{
  "Visitors": [
    { "Provider": "home", "Service": "ssh", "ListenPort": 2222 }
  ]
}
```

访客访问本机 `127.0.0.1:2222`。提供端的 SSH 端口无需对公网开放。
服务名区分大小写，主体 ID 和服务名使用字母、数字、点、下划线或连字符。
修改 P2P 客户端配置后重启客户端。

### 一次授权一台机器：端口交给访客选

不想为内网每台服务各写一行配置，就让提供端放行一组端口，由访客在需要时指定端口。
提供端把端口位写成列表、区间或 `*`：

```json
"P2P": { "Share": { "dev": "3389,8000-8100@laptop" } }
```

访客端在目标后面加一段远端端口，本机端口与它不必相同：

```json
"P2P": { "Connect": { "home/dev/3389": 13389, "home/dev/8080": 18080 } }
```

完整的 `Services` / `Visitors` 写法对应 `Ports` 与 `RemotePort` 两个字段：

```json
{ "Services": [{ "Name": "dev", "LocalHost": "127.0.0.1", "Ports": "*", "AllowVisitors": ["laptop"] }] }
```

```json
{ "Visitors": [{ "Provider": "home", "Service": "dev", "RemotePort": 3389, "ListenPort": 13389 }] }
```

`Ports` 只放开端口，主机名仍取提供端自己的 `LocalHost`，访客无法借这条路访问提供端的
其他主机；不在放行范围内的端口由提供端直接断开该流并留一条 Warning 日志。
不写 `Ports`（或 `Connect` 不带第三段）时行为与之前完全一致：端口固定在 `LocalPort`。
旧版客户端不会带端口，因此只能连未开放 `Ports` 的服务。要真正做到「任意主机名 + 任意端口」
而不逐条登记，用下一节的 SOCKS5 出口。

UDP 直连使用 .NET 自带的网络和加密 API，无需安装额外的库。启用 P2P 后，
FastTunnel 服务端默认在与 `BinPort` 相同的 **UDP** 端口提供地址发现。
在服务端防火墙、容器或云安全组放行该 UDP 端口后，仅使用上面的两行简写也能获取公网映射候选。
如需单独的发现端口，可修改服务端的 `FastTunnel:P2PDiscoveryPort`，并在两端的
`FastTunnel:Server:P2PDiscoveryPort` 填写对应的公网 UDP 端口。服务端填 `0` 可停用监听，客户端填 `0` 可停用查询。
客户端会并行查询主节点、备用节点以及各域名的多个 IPv4/IPv6 地址。两端也可配置
`P2P:StunServers`（最多 8 个），例如
`["stun:stun.cloudflare.com:3478", "stun.l.google.com:19302"]`，以增加独立的发现路径。

可设置 `P2P:DirectUdpPort` 固定直连端口。填写 `"AutoMapPort": true` 可让客户端通过 PCP 或
NAT-PMP 向 IPv4 默认网关申请临时 UDP 映射。两者均不可用时会尝试 UPnP IGD。
发现和映射得到的地址都要通过对端探测才能用于直连。

若任一端的 TCP 端口可从对端访问，可在该端的 `P2P` 下设置 `"DirectTcpPort": 7443`。
客户端会通过信令公布本机的监听地址。如果用网关转发或外部端口不同，再设置
`"DirectTcpEndpoint": "203.0.113.10:7443"`，替换为实际公网地址和端口。
两端可同时尝试 UDP 和 TCP/TLS。新连接优先使用已就绪的 UDP 直连，其次是 TCP/TLS，最后是 WSS。
`P2P:EnableUdpDirect=false` 可单独关闭 UDP 直连，`P2P:EnableDirect=false` 可强制使用中继。

应用可在 WSS 中继就绪后立即连接。日志出现「P2P UDP 直连已就绪」后，新 TCP 连接走直连，
已有连接继续使用建立时的路径。直连中断后，新连接回落到可用中继，客户端在后台重试直连。
协调连接意外中断时，仍健康的直连最多继续承载已有 TCP 流 5 秒。客户端收到撤销授权通知后立即关闭直连。
可在两端的 `P2P:AdditionalRelays` 填写备用 WSS 节点列表，字段与 `FastTunnel:Server`
相同。每个节点都启用 P2P 并配置相同的主体和 Token，客户端会并行预连这些节点。
使用反代和 443 端口时，请透传 WebSocket Upgrade，并对 `/p2p/v1` 使用 TLS 回源。
UDP 地址发现需要独立放行或转发，特别是反代已在 UDP/443 提供 HTTP/3 时。
直连通过 WSS 信令交换临时 ECDH 公钥，随后用 AES-GCM 保护 UDP 数据。
中继数据在两段 WSS 上加密，中继服务器可以接触数据。两端都升级后可使用新版直连协议，
旧客户端仍可使用 WSS 中继。

直连失败时，客户端会记录尝试次数、角色、阶段、耗时和候选计数。常见原因包括
`no_remote_candidates`（缺少对端候选）、`no_probe_response`（发送探测但未收到回应）、
`selection_timeout`（已收到对端探测流量，但候选未完成确认）、`commit_timeout`（最终确认超时）、
`tcp_connect_failed`（TCP 连接失败）和 `tcp_tls_failed`（TLS 握手或会话绑定失败）。
失败日志默认省略候选 IP、Token 和密钥。若已有可用备用候选，最终确认失败时会在同一轮快速重选。

### 主机名 + 任意端口：访客本机 SOCKS5 出口

上一节的 `Connect` 要为一个端口开一个本机监听。想让浏览器、SSH 客户端、数据库工具直接连远端的
任意端口，在**访客端**开一个 SOCKS5 出口：

```json
{
  "FastTunnel": {
    "Server": { "Protocol": "wss", "ServerAddr": "p2p.example.com", "ServerPort": 8443, "CertificateFingerprint": "..." },
    "Token": "visitor-token",
    "P2P": {
      "Socks5": 1080,
      "Socks5Via": [ "home/dev", "work/file" ]
    }
  }
}
```

把应用（或浏览器）的 SOCKS5 代理指向 `127.0.0.1:1080`，目标地址写成
**`服务名.提供端ID.ft`**，端口由应用自己决定——访问提供端 RDP 就是 `dev.home.ft:3389`，
SSH 就是 `dev.home.ft:22`，无需再为每个端口登记监听。只写 `提供端ID.ft` 时，要求该提供端在该
出口里只有一个服务。

* `Socks5` 是本机监听端口（`0` 表示不启用），只会绑定 `127.0.0.1` 与 `::1`，不对外提供服务。
* `Socks5Via` 留空时复用 `Connect`/`Visitors` 里已登记的全部通道；写出来则只放行列出的
  `提供端ID/服务名`（最多 16 个），SOCKS 出口不需要为本机端口占位。
* 支持版本 5、`CONNECT` 命令、域名/IPv4/IPv6 目标；不提供鉴权（只监听回环），不支持 UDP 转发。
* 端口能否连由**提供端**的 `Ports` 决定：`"*@guest"` 或 `"3389,8000-8100@guest"` 才能配合 SOCKS
  选端口；只提供固定 `LocalPort` 的服务，走 SOCKS 也只会连那个端口。主机名一律取提供端自己的
  `LocalHost`，出口不会变成访问提供端内网的跳板。
* 目标名无法解析、端口不在放行范围、隧道尚未就绪时，SOCKS 返回标准错误码（0x02 连接不被允许、
  0x04 目标不可达），应用会看到普通的连接被拒。

至此一条授权覆盖一台机器的任意端口，体验上接近「同一个网段」，但仍不虚拟网卡：只有走这个 SOCKS
代理的流量在隧道里，本机路由表和其他应用不受影响。

### 打洞排障

手动配对的两端都会打印「本地候选 N，STUN 公网映射 M」；托管模式的直连失败日志带有同样的
候选计数。先看这一行判断卡在哪一步。

- `M` 为 0：STUN 没有返回可用地址。公共节点并非全部可达，换一个再试。实测多数网络可用的是
  Cloudflare 和 Google 的节点；查询无响应时，先确认本机 UDP 出网未被防火墙拦截。
- `M` 大于 0 仍失败：对端候选没有送达，通常是某端在对称 NAT 后或运营商未放行 UDP 入站。
  托管模式会自动回落到 WSS 中继，手动模式没有中继，需要换一个 STUN 或改用托管模式。
- 两端都有可互通的 IPv6 时，IPv6 候选不经过 NAT，通常最稳。

映射到的地址不是自己的公网出口时，是虚拟网卡接管了路由，而不是 STUN 出错。用
`curl myip.ipip.net` 取真实出口 IP，与日志里的候选对比：候选中出现 `100.64.0.0/10`、
`169.254.0.0/16`、`198.18.0.0/15`，或出口 IP 与候选完全不同，说明 VPN/TUN 在转发探测流量。
退出 VPN 后重新生成邀请。仅开启 HTTP/SOCKS 代理不影响打洞，代理不接管 UDP 默认路由。

其他容易忽略的前提：手动配对要求两端系统时间相差在 30 秒内，否则会报「已过期或两端时间不同步」；
提供端收到应答后只有约 25 秒打洞窗口，超时请重新走一遍配对；两端都要保持命令窗口开启，关闭即拆除映射。

## 感谢以下贡献者
<a href = "https://github.com/FastTunnel/FastTunnel/graphs/contributors">
  <img src = "https://contrib.rocks/image?repo=FastTunnel/FastTunnel"/>
</a>  

## QQ群交流群
加群交流FastTunnel相关的使用问题、bug、需求等  
群1：798672272  
群2：935214348  
群3：768089177  

## License
Apache License 2.0

## 特别声明

本项目已加入 [dotNET China](https://gitee.com/dotnetchina)  组织。<br/>

![dotnetchina](https://images.gitee.com/uploads/images/2021/0324/120117_2da9922c_416720.png "132645_21007ea0_974299.png")
