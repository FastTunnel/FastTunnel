# FastTunnel 违法内容自动发现与封禁 · 技术方案

> 设计评审稿 v0.1 · **分阶段落地中**（S3 内容检测暂缓）
> 文档正文是设计时的方案描述，实际落地状态以 **附录 C** 为准；附录 A 的 nginx 配置仍为示意、未写入仓库。

| 项 | 值 |
| --- | --- |
| 状态 | 设计评审稿；P0 / S0 / S1 / S2 已实施，S3 因法务前置暂缓 |
| 日期 | 2026-11-13 |
| 代码基线 | `v3` 分支 `02ff0ca`（含 `8db239d` 控制协议结构化、`17c4e74` mux、`a20c6ac` TLS） |
| 运行环境 | .NET 10 / FastTunnel 3.3.0 |
| 适用范围 | 服务端合规管控。不含客户端改造、内容安全服务选型定稿、用户协议法务文本 |
| 维护约定 | 改造落地后须同步更新 §3、§8 的文件行号引用 |

---

## 0. 结论修订（重要）

早期口头评估基于 `a20c6ac` 之前的快照，以下三点按当前基线更正：

1. **「1270 是纯明文端口、HTTPS 一律不可解析」— 已过时**。`ListenOptionsSwapExtensions.cs:34` 已把 `TlsMiddleware` 注册为连接最外层：按首字节 `0x16` 识别 ClientHello，**在服务端原地完成 TLS 握手**，解密后的 `TlsDuplexPipe` 回挂 `context.Transport`（`TlsMiddleware.cs:119-132`）。后续 `FastTunelProtocol` / `ForwarderMiddleware` 看到的是明文。
2. **「URL 路径看不到」— 部分过时**。`FastTunelProtocol.cs:109, 148-151` 已新增 `Path`（请求行路径，不含 query）。但解析器只保留 `Host`，其余 Header 全部丢弃；Body 与响应体仍是裸 Pipe 对拷。
3. **数据面主路径已切 mux**。`ForwarderMiddleware.waitSwapViaMux:274-327` 是默认路径，`waitSwap:81-210` 为回退路径。**任何旁路镜像与熔断都必须同时覆盖这两条**，只改一处会漏掉绝大多数流量。

**不变的核心判断**：TLS 终止点不在自己手上时（用户内网自跑 HTTPS、CONNECT 正向代理、域名 CNAME 到 CDN），内容检测在密码学上不可能，不应投入；管控重心仍是准入、熔断、留证、主动巡检。

---

## 1. 目标与非目标

- **目标**：把「发现 → 立即停止传输 → 消除 / 防扩散 → 保存记录 → 报告」做成可自动运行的闭环。
- **非目标**：不在链路上破解第三方 TLS；不承诺 100% 识别；不替代人工复核；不做用户隐私数据的越界采集。

---

## 2. 合规依据

| 依据（现行法） | 要求 | 对应设计 |
| --- | --- | --- |
| 《网络安全法》2025 修正、2026-01-01 施行 **第 49 条** | 发现法律、行政法规禁止发布或传输的信息，立即停止传输、采取消除等处置措施、防止信息扩散、保存有关记录、向有关主管部门报告 | §6.4 熔断链、§6.5 留痕、§9 上报 |
| **第 51 条** | 建立网络信息安全投诉、举报制度，公布投诉举报方式，及时受理并处理 | §6.3 举报接口、§9 申诉通道 |
| **第 23 条（三）** | 采取监测、记录网络运行状态与网络安全事件的技术措施，相关网络日志留存不少于六个月 | §6.5 审计日志 |
| **第 26 条** | 为用户办理网络接入、域名注册、信息发布等服务时，要求用户提供真实身份信息 | §6.1 实名准入 |
| 《个人信息保护法》 | 处理个人信息应明示目的、方式、范围并取得同意 | **法律前置条件**：内容检查须写入用户协议 / 隐私政策并获授权 |

> 引用的是 2025 年 10 月 28 日修正、2026 年 1 月 1 日施行的现行版本；旧资料中「第 47 条 / 第 49 条 / 第 21 条 / 第 24 条」为修正前条号，内容对应关系不变。

---

## 3. 现状审计（当前代码）

> 本节是设计时（2026-11-13）的代码快照。P0 / S0 / S1 / S2 的落地状态与结论修订见 **附录 C.1 / C.2 / C.3**，
> 后续运维最小集与一户一 token 见 **附录 C.4**。下文的行号与「现状」描述以附录 C 为准。

| 位置 | 现状 | 风险 / 可用之处 |
| --- | --- | --- |
| `TlsMiddleware.cs:119-132` | 服务端终止 TLS，明文 Pipe 回挂 | **可用**：现成的 L7 可解析入口，无需外挂 nginx |
| `appsettings.json:45-54` | `Tls:Enabled=true`，`AutoGenerate=true` 自签证书 | 公网浏览器不信任自签证书 → 需 `CertFile` 配受信任 CA 证书，SAN 覆盖 `*.WebDomain` |
| `FastTunelProtocol.cs:136-207` | 逐行解析至空行，保留 `Method / Path / Host` | 请求头可做低成本检测；其余 Header 丢弃 |
| `ForwarderMiddleware.cs:159-160`、`294-295` | 双向 `PipeReader.CopyToAsync` 零拷贝 | 明文旁路镜像的唯一插入点，需 Tee 化 |
| `appsettings.json:36` | `EnableForward: true`（**C.2 已改 false**） | TCP / UDP 端口转发完全黑盒，最大不可审计出口 |
| `LoginHandler.cs:63-65` | `TODO:validateDomain` + `AddOrUpdate` 覆盖 | 可任意声明他人域名，域名劫持 + 越权映射 |
| `WebInfo.cs:28-31` | `LogOut()` 空实现 | 下线后 `WebList` 残留，封禁必须显式 `TryRemove` |
| `FastTunnelClientHandler.cs` `checkToken` | `Tokens` 为共享静态串 | 无法把违规行为定位到具体账号 |
| `DefaultServerConfig.cs` `WebAllowAccessIps` | 配置项零读取点 | 死配置，需接线或删除 |
| `FastTunnelServer.cs:45` | 新增 `MuxClients` 会话表 | 封禁需一并清理会话绑定 |

---

## 4. TLS 可见性矩阵

| 场景 | TLS 终止点 | 服务端可见内容 | 结论 |
| --- | --- | --- | --- |
| 公网 HTTP 访问 `http://host:1270` | 无 | 完整请求 + 响应明文 | 可检测 |
| 公网 HTTPS 直连 1270（复用现有 `TlsMiddleware`，配正式证书） | **自有服务端** | 完整请求 + 响应明文 | 可检测，**优先方案** |
| 前置 nginx `proxy_pass http://127.0.0.1:1270` | 自有网关 | 完整明文 | 可检测（等价，多一跳） |
| 内网服务自跑 HTTPS / `stream + ssl_preread` 四层透传 | 用户内网 | 仅 IP、SNI、ALPN、TLS 指纹、包长时序 | 仅域名级管控 |
| CONNECT 正向代理 / 域名 CNAME 到 CDN | 第三方 | `CONNECT host:443` 或 CDN 回源 IP；SNI 还可能被 ECH 加密 | 仅准入 + 主动巡检 |

补充说明：

- TLS 1.3 中服务端证书本身也是加密的；ECH（Encrypted Client Hello）可进一步隐藏 SNI，连域名级封禁都可能失效。
- 证书固定、mTLS、HSTS、Certificate Transparency 使「给公网用户装根 CA 做 TLS 中间人」不可行；擅自解密用户通信另有无视授权的法律风险。
- **JA3 / JA4 指纹、包长与时序只能作辅助风控信号，不得作为违法判定或封禁依据。**

---

## 5. 总体架构

1. **准入底座**：实名、一户一 token、域名归属 / 备案校验、协议与端口白名单。
2. **可解析层**：复用 `TlsMiddleware` 的明文流，把请求头与有限 Body / 响应做旁路镜像。
3. **检测层**：规则 / 情报 + 内容安全 API + 主动巡检 + 举报，异步非阻塞。
4. **处置与留痕**：`BanRegistry` 驱动熔断链，全量审计日志，人工复核与上报。

---

## 6. 详细设计

### 6.1 准入

- `EnableForward=false`（P0，堵住黑盒出口）。
- `Tokens` 改为一户一 token 并绑定实名主体。
- 实现 `validateDomain`：DNS TXT 归属验证 + ICP 备案查询 + 保留域名黑名单；把 `AddOrUpdate` 改为「已被他人占用则拒绝」。
- `WebAllowAccessIps` 接线或删除。
- `ForwardConfig` 增加协议 / 端口白名单，默认只允许 HTTP 类端口。

### 6.2 可解析层

- 证书换为受信任 CA（正式签发或 ACME），SAN 含 `*.${WebDomain}`。
- 在 `InitializerMiddleware` 之后、`ForwarderMiddleware` 之前插入 `GuardMiddleware`：读取 `IFastTunnelFeature` 的 `Host / Path / Method` 做请求头级检测与封禁判定，**不消费 Pipe 数据**（沿用 `AdvanceTo(Start, Start)`，参见 `TlsMiddleware.cs:75-77` 的踩坑注释：写成 `AdvanceTo(Start, End)` 会让下一次 `ReadAsync` 一直等新数据，连接挂死）。
- Body 与响应级检测放在 §6.3 的 Tee 处完成，避免破坏 mux 流控与零拷贝快路径。

### 6.3 检测

- **旁路镜像**：把 `waitSwap` 与 `waitSwapViaMux` 的双向 `CopyToAsync` 换为 Tee Pipe，异步送出；单请求上限（如首 256 KB）、只取 `text/*` / `application/json` / 图片与视频前若干帧，脱敏 `Cookie` / `Authorization`，非阻塞、失败不影响转发。
- **判定**：本地关键词 / 正则 + 违规 URL 与域名情报 + 文件哈希库，叠加第三方内容安全 API（文本 / 图片 / 视频）；打分制，不因单点命中即封。
- **主动巡检**（加密场景主抓手）：定时遍历 `WebList` 的全部 host，以普通访客身份用 `HttpClient` 正常完成 TLS 握手，抓取首页与关键路径送审。不窃听链路，看到的就是公网所见，合规上更干净。
- **举报**：对外公布举报入口，落库并触发人工复核队列（第 51 条要求）。

### 6.4 处置（顺序不可颠倒）

`BanRegistry`（单例，内存 + 持久化）维护 token / host / 账号三级黑名单。熔断链：

1. 立封禁标记 → 拦住所有新请求（`GuardMiddleware` 返回 `451 Unavailable For Legal Reasons`，不进转发）；
2. 摘除 `WebList` 全部映射、`ForwardList` 监听、`MuxClients` 会话绑定（**显式清理，不能依赖 `LogOut()`**）；
3. `webSocket.Abort()` 断控制面，关闭 mux 会话，活动连接随 CTS 取消；
4. 保存证据快照与访问日志；
5. 通知人工复核 / 必要时上报主管部门。

### 6.5 留痕

审计字段：时间戳、真实访客 IP（`X-Forwarded-For` 或 `RemoteIpAddress`）、Host、Path、Method、token / 账号、上下行字节数、命中规则 ID、评分、证据 ID、处置动作与操作人。留存 ≥ 6 个月，证据快照只读、防篡改。

---

## 7. 数据模型与接口（拟）

`BanRecord`：`Id / SubjectType(token|host|account) / Subject / RuleId / Score / EvidenceId / Level / CreatedAt / ExpiresAt / Operator / ReviewState / Reason`。

| 端点 | 方法 | 用途 |
| --- | --- | --- |
| `/api/abuse/report` | POST | 举报受理（对外，公布入口） |
| `/api/abuse/ban` | POST | 封禁（JWT，写审计） |
| `/api/abuse/unban` | POST | 解封（JWT + 复核记录） |
| `/api/abuse/reviews` | GET | 待复核队列 |
| `/api/abuse/audit` | GET | 审计检索 |

> **实际落地（C.3）**：路由沿用项目的 `api/[controller]/[action]`，大小写不敏感，
> 例如 `/api/Abuse/Ban`。除 `Report`（`[AllowAnonymous]`，对外举报）外全部要求 JWT。
> 另有 `List`（黑名单快照）与 `Review`（人工复核）。`Ban` 只能创建 `Block` 级熔断；
> `Permanent` 会被降级，唯一升级入口是 `Review`（`Confirmed` + `Elevate`）。

---

## 8. 改造点清单

> 已完成项不在此重复标注，逐项状态见 **附录 C.1 / C.2 / C.3 / C.4**。下表保留原始规划顺序，便于对照阶段划分；
> 其中「ForwarderMiddleware 两条路径 Tee 镜像」属于 S3 内容检测，因法务授权未定**未实施**，S2 改用连接层前置熔断达成封禁目标。
> P0 的「一户一 token」已在 C.4 落地（配置化主体目录），**实名 / 账号体系仍未做**。

| 优先级 | 文件 | 动作 |
| --- | --- | --- |
| P0 | `FastTunnel.Server/config/appsettings.json` | `EnableForward=false`；配受信任证书 |
| P0 | `FastTunnel.Core/Handlers/Server/FastTunnelClientHandler.cs` `checkToken` | 一户一 token + 黑名单校验 |
| P0 | `FastTunnel.Core/Handlers/Server/LoginHandler.cs:63-65` | 域名归属 / 备案校验，禁止覆盖他人映射 |
| P0 | `FastTunnel.Core/Server/FastTunnelServer.cs`、`Models/WebInfo.cs` | 下线时清理 `WebList`；提供按 client 反查 host 的能力 |
| P1 | 新增 `GuardMiddleware` | 请求头检测 + 451 熔断，注册在 Initializer 之后 |
| P1 | `Forwarder/Kestrel/MiddleWare/ForwarderMiddleware.cs` 两条路径 | Tee 旁路镜像（**双路径**） |
| P1 | 新增 `BanRegistry` / `BanService` / `AuditLog` | 熔断链与留痕 |
| P1 | `FastTunnel.Api/Controllers/` 新增 Controller | §7 接口 |
| P2 | 新增 `CrawlerWorker` | 主动巡检 |
| P2 | `FastTunnel.Core/Config/DefaultServerConfig.cs` | 清理 / 接线 `WebAllowAccessIps` |

---

## 9. 分级处置与误报控制

| 级别 | 触发 | 自动动作 | 人工动作 |
| --- | --- | --- | --- |
| L0 观察 | 低分命中 | 记录、告警 | — |
| L1 限速 | 中分 / 异常上传 | 限流、抽样送审 | 抽检 |
| L2 熔断 | 高分 / 举报初核 / 情报命中 | 451 + 撤映射 + 断链 + 留证 | 复核（建议 SLA ≤ 2h） |
| L3 永封 | 复核确认 | 吊销 token、拉黑主体 | 上报主管部门、通知用户、申诉通道 |

自动系统只负责「熔断 + 留证」，**永久封禁与上报必须经人工复核**；判定规则不自建黑话词库，使用行业违规 URL / 哈希库与有资质的内容安全服务。

---

## 10. 实施计划与验收

| 阶段 | 内容 | 验收标准 |
| --- | --- | --- |
| S0 准入收紧 | P0 全部 | 裸转发无法建立；共享 token 被拒；未验证域名无法映射 |
| S1 明文可解析 | 正式证书 + `GuardMiddleware` | 公网 HTTP / HTTPS 请求均能看到 `Host / Path`；自签证书告警消失 |
| S2 熔断闭环 | BanRegistry + 双路径改造 | 封禁后 1s 内新请求全 451；`WebList` / `MuxClients` 无残留；mux 与回退路径均生效 |
| S3 检测巡检 | 旁路送审 + 主动巡检 + 举报 | 送审失败不影响转发；mux 流控无退化；巡检覆盖 `WebList` 全量 |
| S4 复核上报 | 分级 + 审计 + 申诉 | 日志可检索、留存 6 个月；证据链完整可导出 |

每阶段回归基线用例：HTTP 域名路由、TCP / UDP 转发、mux 3MB 双向一致性、40 并发全 200、80s 空闲不误杀、断线重连。

**进度（2026-11-14）**：S0 / S1 / S2 已落地，见附录 C。S2 的「双路径改造」验收改为「GuardMiddleware 挂在 `waitSwap` 分叉之前 + 测试锁住两条路径都被拦截」，不改动零拷贝数据面。**S3（旁路送审 / 主动巡检）按本轮决定暂缓**，法务授权未定前不启动。S4 的复核与审计接口已具备，但上报主管部门与申诉流程仍未接线。

主线之外，本轮并行完成「运维最小集」与「一户一 token」（**附录 C.4**），属于 frp 差距支线：前者补上可观测与限速，后者让按主体封禁不再误伤；两者都不改变上述 S3 的暂缓决定。

---

## 11. 风险与未决

- 用户协议 / 隐私政策的授权文本未定，是内容检查的**法律前置条件**，需法务确认后再上 S3。
- 内容安全服务选型、违规判定标准与词库来源未定。
- 巡检频率、成本与对用户内网服务的负载影响待评估。
- 残余盲区：用户内网自跑 HTTPS、域名 CNAME 到 CDN、ECH 隐藏 SNI → 只能靠准入管控与巡检覆盖。
- `TlsMiddleware` 的设计意图是「隧道客户端通道加密」，实现上并不区分连接来源。把它正式用于公网 HTTPS 属于语义扩展，需评估：控制面连接与公网访客共用证书、握手失败日志噪声、扫描器触发的 `0x16` 误判。
- 误封申诉 SLA、证据保存责任人、上报流程责任部门未定。S2 已提供复核与审计接口，但上报主管部门与申诉通道仍是流程对接，不在代码里。
- `BanRegistry` / `AuditLog` 是**单机内存 + 本地文件**（`config/bans.json` / `config/audit.jsonl`）：多实例部署时封禁只在单个实例生效，必须换成共享存储；证据链无签名 / 防篡改，留存 ≥ 6 个月由部署层日志归档与权限保证。
- 匿名 `Report` 接口尚未加网关层限流与人机校验，上线前需补，否则复核队列可被刷爆。

**决策记录（2026-11-14）**

1. 公网 HTTPS **采用外挂 nginx 终止 TLS**（附录 A），不扩展 `TlsMiddleware` 语义。理由：控制面连接与公网访客共用同一证书会带来边界混淆、握手失败日志噪声与扫描器 `0x16` 误判；nginx 方案证书隔离、语义干净。代价是多一跳，且必须透传 `Host` 与真实 IP —— 这两项已在 S1 落地。S1 的请求头解析工作在明文流上，与终止点选择无关，故可先行。
2. 用户协议中内容检查的授权范围与告知方式 **仍未定**，是进入 S3（旁路送审 / 主动巡检）前的法务前置条件。
3. S2 采用**连接层前置熔断**，而不是「在两条 `CopyToAsync` 上做 Tee 镜像后判定」：封禁不需要读 Body，挂在 `waitSwap` 分叉之前的 `GuardMiddleware` 一次判定即覆盖 mux 与回连两条数据面，既不改零拷贝数据面，也不会漏改一条路径。内容检测（S3）仍需 Tee，待法务授权后单独实施。
4. **S3 短期内不做**（旁路送审、主动巡检），优先补运维最小集与一户一 token：前者不依赖法务决策且是当前 frp 差距里最大的短板，后者解决 §11 / C.3 记下的「按 token 封禁误伤共用凭据会话」。本轮成果见 C.4；S3 恢复时仍以「用户协议授权 + 内容安全服务选型」为前置。
5. 「运维最小集」范围定为 `/healthz`、`/metrics`（Prometheus 文本）、按代理带宽统计与令牌桶限速；**不做** Dashboard / Admin UI、配置热加载界面与 `Throttle` 级别的自动降速动作（该级别仍只记录，见 C.4）。

---

## 附录 A · nginx 参考配置（示意，未落地）

```nginx
server {
    listen 443 ssl http2;
    server_name *.example.com;
    ssl_certificate     /etc/ssl/example.com.pem;
    ssl_certificate_key /etc/ssl/example.com.key;

    location / {
        proxy_pass http://127.0.0.1:1270;          # TLS 终止，明文回源 → 可解析
        proxy_set_header Host              $host;  # 必须透传，否则 FastTunelProtocol 无法路由
        proxy_set_header X-Forwarded-For  $proxy_add_x_forwarded_for;
        proxy_set_header X-Real-IP        $remote_addr;
    }
}
```

禁止改用 `stream { ssl_preread on; }` 四层透传——那会退回 §4 的不可解析场景。

对应服务端配置（示意，S1 已落地）：

```json
{
  "FastTunnel": {
    "TrustedProxyIps": [ "127.0.0.1", "::1" ],
    "WebAllowAccessIps": []
  }
}
```

`TrustedProxyIps` 只填**自己的** nginx 回源地址：nginx 与 FastTunnel 不在同一主机时改成实际回源网段，
否则 XFF 会被忽略、真实 IP 退化成 nginx 的地址，白名单会把所有访客判为同一来源。
`WebAllowAccessIps` 为空表示不限制；非空时按还原后的真实访客 IP 判定，支持 IP 与 CIDR。

PROXY protocol（v1/v2）暂不实现：本架构是 HTTP 反代、XFF 已覆盖；四层透传正是 §4 的不可解析场景，不引入。

---

## 附录 B · 主动巡检伪代码（示意，未落地）

```text
every T:
  for host in FastTunnelServer.WebList.Keys:
      resp = HttpClient.GetAsync("https://" + host + "/")   # 正常 TLS 握手、正常解密
      submit(resp.Headers, first N KB of body) -> ContentSecurityAPI
      if score >= L2.threshold: BanService.Ban(host, evidenceId)
```

---

## 附录 C · 落地记录（2026-11-14）

> §3 / §8 是 2026-11-13 的代码快照。下列项已实施，文中再出现「TODO」「死配置」等描述时以本节为准。
> 本节按 C.1（P0）/ C.2（S0 + S1）/ C.3（S2）分阶段记录；后段覆盖前段的阶段结论。

### C.1 P0 准入收紧

| 项 | 状态 | 落地内容 |
|---|---|---|
| `WebAllowAccessIps` 死配置 | 先删除、后重新接线 | 该配置项原本从无任何读取点，配了也不会生效，属于误导性安全开关，P0 阶段先从 `IServerConfig` / `DefaultServerConfig` / `appsettings.json` 移除；**C.2 的 S1 已按 §6 在 L7 层重新设计并真正接线** |
| 新增 `WebDomainBlacklist` | 已实现 | 服务端配置，支持精确域名与 `.suffix`（含子域）两种写法；命中后拒绝登记映射 |
| `TODO:validateDomain` | 部分实现 | 新增 `HostNameValidator`：域名语法/字符集、CRLF 与 URL 注入、保留后缀、IP 字面量、平台 `WebDomain` 子域抢注、运营黑名单、SubDomain 单标签约束。**DNS 所有权验证（TXT 挑战）与 ICP 备案查询仍未实现**，是合规 S0 的遗留项 |
| `AddOrUpdate` 覆盖他人映射 | 已修复 | `FastTunnelServer.TryRegisterWeb` 改为占用判定：他人在线占用拒绝，同一会话刷新允许，旧会话离线允许接管 |
| 下线后 `WebList` / `ForwardList` 残留 | 已修复 | `WebInfo` 记录 `Host`；`TunnelClient.Logout()` 按 (key, 实例) 精确摘除，不误删同名新归属；`TunnelClient.IsOffline` 兜住清理与重连的竞态 |
| README 与实现不一致 | 已修正 | 4 份 README 同步 mux / TLS / WS / IPv6 能力；删除不实的「域名白名单限制」勾选；v2 `ClientSettings/SSH` 示例换成 v3 `FastTunnel` 结构；修正 .NET 5、1271 端口等过时信息 |

### C.2 S0 / S1 准入收尾与 L7 可解析层

| 项 | 状态 | 落地内容 |
|---|---|---|
| `EnableForward=false` | 已实现 | `FastTunnel.Server/config/appsettings.json` 默认值改为 `false`，注释与 4 份 README 写明「裸 TCP/UDP 转发不经过 HTTP 解析层，既拿不到真实访客来源、也无法内容审计」；`DefaultServerConfig` 原本默认即为 false。需要端口转发的部署必须显式开启 |
| token 可追溯到会话 | 部分实现 | 新增 `ClientTokenValidator`：定长比较校验，并把命中的 token 写入 `TunnelClient.Token`；`FastTunnelClientHandler` 在控制面与 WebSocket MUX 承载复用同一校验，S2 可经 `WebInfo.Client.Token` 定位主体。**共享静态 `Tokens`、一户一 token 建表与实名仍未实现** |
| L7 请求元数据 | 已实现 | `FastTunelProtocol` 保留 `Method` / `Host` / `Path` 与审计白名单请求头（`User-Agent` / `Content-Type` / `Content-Length` / `Referer` / `Host` / 转发头；**Cookie、Authorization 等凭据头不入库**），沿用 `AdvanceTo(Start, Start)` 一字节不消费；顺带修掉 `Host:value` 无空格写法会 `IndexOutOfRange` 的缺陷；`IFastTunnelFeature` 增加 `Path` / `Headers` / `ClientIp`，删除零使用的 `HasReadLInes` |
| 真实访客 IP（XFF 防伪造） | 已实现 | 新增 `ClientIpResolver`：仅当 TCP 对端落在 `TrustedProxyIps` 时才采信 `X-Forwarded-For` / `Forwarded` / `X-Real-IP`；支持多级代理链（从右往左跳过可信代理）、IPv4-mapped IPv6 归一化、IPv4/IPv6 的 IP 与 CIDR 规则；规则集按配置数组引用缓存，热加载自动失效 |
| 访问来源 IP 白名单 | 已实现（重新接线） | `WebAllowAccessIps` 重新加入 `IServerConfig` / `DefaultServerConfig` / `appsettings.json`，这次有真实读取点：`ForwarderMiddleware.OnConnectionAsync` 在 `waitSwap` 分叉**之前**按真实 IP 判定，拒绝时回 `403 Connection: close`。拦在分叉前 ⇒ mux 与回连两条路径同时覆盖，不会重演 §0 的漏路径问题 |
| `WebInfo.LogOut()` 空实现 | 已删除 | 会话级清理统一到 `TunnelClient.Logout()`；`ForwarderMiddleware` 两处空调用删除，`ForwardDispatcher` 过时 TODO 更新，`TunnelClient` 注释同步 |
| 4 份 README | 已同步 | 特性列表、两个「白名单」的区别、`TrustedProxyIps` 配置要求（切勿配 `0.0.0.0/0`）、端口转发默认关闭与残余风险 |

**S1 决策已定**：公网 HTTPS 走外挂 nginx 终止 TLS（§11、附录 A），不扩展 `TlsMiddleware`；PROXY protocol 暂不实现，理由见附录 A。

**截至 S1 仍未实施**（其中熔断相关项已在 C.3 的 S2 落地）：一户一 token 建表与实名、`GuardMiddleware` 与 451 熔断、旁路送审、主动巡检、`BanRegistry` / 审计日志 / 举报接口、正式受信任证书、用户协议授权文本（法务前置，进入 S3 前必须定）。

**验证**：`dotnet build FastTunnel.sln` 0 错误；`dotnet test FastTunnel.Core.Tests` **251/251 通过**，其中 P0 新增 48 例，本轮新增 `ClientIpResolverTests` / `RequestMetadataTests` / `ClientTokenValidatorTests` / `VisitorAllowListTests` 共 44 例（覆盖 XFF 伪造、多级代理链、CIDR、凭据头脱敏、字节不消费、mux 端点、403 拦截与双路径前置）。

---

### C.3 S2 熔断闭环（同日）

**范围**：只做「熔断 + 留证 + 复核处置」，不做内容检测（S3 需法务授权前置）。全部改动仍在工作区，未提交。

| 项 | 状态 | 落地内容 |
|---|---|---|
| `GuardMiddleware` | 已实现 | 新增连接层中间件，注册在 `Initializer` 之后、`Forwarder` 之前，链路为 `Tls → Initializer → Guard → Forwarder`。统一承担 451 封禁与 403 白名单判定，访问白名单从 `ForwarderMiddleware` 迁出。拦截点在 `waitSwap` 分叉**之前**，mux 与回连两条数据面天然同时覆盖，不需要分别改两条路径 |
| 摘映射后仍能 451 | 已修复关键陷阱 | `FastTunelProtocol.IsFastTunnel` 原本依赖 `MatchWeb != null`，而熔断必须摘掉 `WebList` 映射，两者直接冲突。现由 `FastTunnelServer.IsHostBanned` 在 Host 匹配失败时设置 `hostBanned`，仍生成 Feature，Guard 才能回 451；否则会退化成 404 |
| `BanRegistry` | 已实现 | token / host / account 三级黑名单；**token 只存 SHA-256 前 8 字节指纹**，状态文件里不落明文凭据；host 大小写 / 尾点归一；`GroupId` 把一次熔断产生的主体与关联域名串成一组，可整组解封 / 复核；`config/bans.json` 原子替换持久化，重启不失效 |
| `AuditLog` | 已实现 | 内存环形缓冲（默认 10000）+ `config/audit.jsonl` JSONL 追加。字段含时间戳、真实访客 IP、Host / Path / Method、token 指纹、BanId、RuleId / Score / EvidenceId、动作与操作人；凭据头从源头就不入库 |
| `BanService` 熔断链 | 已实现 | 严格按 §6.4：① 立标记 → ② 关联域名一并拉黑 → ③ `TerminateClient` 摘 `MuxClients` / `WebList` / `ForwardList`、停监听、关 mux、`Abort` 控制面 → ④ 写审计 → ⑤ 日志告警 + `ReviewState=Pending` 进入人工复核队列 |
| 自动系统不能永封 | 已实现 | `Ban` 收到 `RequestedLevel=Permanent` 一律降级为 `Block` 并在返回值标记；唯一升级路径是 `Review`（`Confirmed` + `Elevate`）；复核驳回视为误封，整组立即解封 |
| 处置接口 | 已实现 | `FastTunnel.Api/Controllers/AbuseController.cs`：`Ban` / `Unban` / `Review` / `List` / `Reviews` / `Audit`（JWT）+ `Report`（`[AllowAnonymous]`，对外举报入口，第 51 条）。路由沿用 `api/[controller]/[action]` |
| 举报 | 已实现（不自动封禁） | 只生成 `Observe` 级记录进待复核队列并写审计，不触发熔断；接口有长度限制，真实 IP 经 `TrustedProxyIps` 还原 |
| 准入联动 | 已实现 | `FastTunnelClientHandler.checkToken` 拒绝被封 token 重新接入（控制面与 WS MUX 承载共用）；`LoginHandler.TryAddMapping` 拒绝被封域名重新登记，防止一次重连绕过熔断 |
| 配置与 DI | 已实现 | `FastTunnel:Abuse:Ban:StateFile`、`Abuse:Audit:FilePath` / `MemoryCapacity`；`BanRegistry` / `AuditLog` / `BanService` 注册为单例，并有 DI 容器解析冒烟测试 |

**设计取舍**：S2 没有在两条 `CopyToAsync` 路径上做 Tee 镜像（§8 P1 的原始设想），因为内容检测属于 S3 且法务授权未定；熔断改为在连接层前置拦截，避免为了封禁去改动零拷贝数据面，也避免只改一条路径漏流量。§10 中「S2 双路径改造」的验收改用「Guard 在分叉前拦截 + 测试锁住」达成。

**部署注意**

1. `StateFile` / `FilePath` 是**单机本地文件**，多实例部署必须换成共享存储，否则封禁只在单个实例生效。
2. 审计留存 ≥ 6 个月、证据只读防篡改由部署层日志轮转 / 归档 / 文件权限保证，代码里没有签名链。
3. 别把 `StateFile` 配成空（重启后封禁全失效）；`bans.json` 与 `audit.jsonl` 要限制文件权限。
4. `Report` 匿名开放，上线前应加网关层限流 / 人机校验，避免复核队列被刷爆。
5. **按 host 封禁只影响该域名**：同一客户端仍可用新域名重新登记。写本条时 `Tokens` 仍是共享静态串、按 token 封禁会误伤共用者，`Account` 类型虽已建模但 `FindAffectedClients` 返回空；**C.4 落地一户一 token 后已改变**：`Clients[].Id` 成为可靠主体，按 `account` 封禁会精确命中该主体的会话并撤销其全部域名。仍在使用旧版共享 `Tokens` 的部署，误伤风险依旧存在。
6. 451 判定依赖 `FastTunelProtocol` 解析出的 `Host`；畸形请求、缺少 Host 头或非 HTTP/1.x 明文流量不会命中，属 §4 的既有盲区。

**C.3 时点仍未实施**（后续状态见 C.4）：旁路送审与主动巡检（S3，法务授权前置）、正式受信任证书、一户一 token 与实名、DNS TXT / ICP 备案校验、上报主管部门与申诉通道的流程对接（S4）、运维最小集。`Throttle` 级别当时已建模但限速动作未实现。

**C.3 验证**：`dotnet build FastTunnel.sln` 0 错误；`dotnet test FastTunnel.Core.Tests` **280/280 通过**。本轮新增 `BanRegistryTests` / `GuardMiddlewareTests` / `BanServiceTests` / `AuditLogTests` / `ServiceRegistrationTests` 共 29 例，并改造 `VisitorAllowListTests` 适配新中间件链。覆盖：摘映射后仍 451、451 优先于 403、双路径前置、token 指纹不落明文、状态文件重启恢复（不二次哈希）、熔断链顺序与整组解封、自动永封降级、举报不自动封禁、DI 容器可解析。

---

## 附录 C.4 · 运维最小集与一户一 token（2026-11-14）

> 本轮按用户决定**跳过 S3**（旁路送审 / 主动巡检暂缓），优先补齐 frp 差距支线里的运维能力与主体身份。
> 与合规主线的关系：按代理流量统计为 §6.5 的访问日志提供了字节量与连接数字段；一户一 token 让 §6.4 的按主体封禁不再误伤。

### 一户一 token

| 项 | 落地内容 |
| --- | --- |
| 配置 | `FastTunnel:Clients[]`：`Id` / `Token` / `SubDomain` / `AllowedHosts` / `BandwidthLimit` / `Enabled`。`Id` 与 `Token` 必须唯一，重复或非法条目跳过并在 `/healthz` 的 `credentials.warnings` 报出 |
| `ClientTokenDirectory` | token → 主体的只读快照，定长比较、`IOptionsMonitor` 引用变化触发热加载；**配置了 `Clients` 段就强制校验 token**，即使条目全部停用也不退回无鉴权模式 |
| 兼容 | 旧版共享 `Tokens` 仍接受，但标成 `legacy:sha256:*`，不参与单会话强制；按 token 封禁仍会误伤所有共用者 |
| 会话绑定 | `TunnelClient.Credential` / `Subject`；`EnforceSingleSession`（默认 true）在 `clientsLock` 内原子找出同主体旧会话并返回给调用方断开，两个并发登录不可能同时都认为自己是唯一会话 |
| 域名授权 | `LoginHandler` 校验 `SubDomain` 与 `AllowedHosts`（精确 + `.suffix`）；未认证会话保持原有不限制行为 |
| 封禁联动 | `BanRegistry.Match` 增加 account 维度；`BanService.FindAffectedClients` 按 `Subject` 精确匹配具名主体；`api/Abuse/Ban` 传 `SubjectType=account` 即可停掉该主体全部域名 |
| 诚实边界 | 这是**配置化主体目录**，不是账号体系 / 实名。无注册、无凭据轮换、无数据库；`AllowedHosts` 也不是 DNS 所有权验证，TXT 挑战与 ICP 备案查询仍未做 |

### 运维最小集

| 项 | 落地内容 |
| --- | --- |
| `GET /healthz` | 匿名 JSON：uptime、版本、在线客户端 / 映射数 / 活动连接 / 待回连、请求与拒绝计数、累计字节、凭据配置告警。不含 token 明文 |
| `GET /metrics` | Prometheus 0.0.4 文本：进程级 gauge、按 `proxy` / `client` / `direction` 的累计字节与连接数、限速等待时长、当前速率上限。标签值转义；`Ops:IncludeClientLabels=false` 可去掉主体标签 |
| `GET /api/Ops/Traffic` | JWT，按代理的 JSON 明细 |
| 计量内核 | `TrafficMeter`（单例）按代理累计字节 / 连接 / 限速等待；`MeteredPipeWriter` 同时覆盖 `WriteAsync` 与 `GetMemory → Advance → FlushAsync` 两条写入方式且不重复计数；`MeteredStream` 覆盖 Stream 写入与双向读取 |
| 覆盖路径 | HTTP 回连、HTTP mux、TCP 转发回连、TCP 转发 mux 四条数据面全部接入；UDP 端口转发通过 `WrapBidirectional` 统计上下行。**没有改动 `CopyToAsync` 的零拷贝搬运逻辑**，只在写入目标外包一层装饰器 |
| 限速 | 令牌桶按「代理 + 方向」共享，优先级 `Bandwidth.PerProxy` > `PerClient` > `Clients[].BandwidthLimit` > `DefaultBytesPerSecond`，0 = 不限速；`BurstSeconds` 控制突发容量；连接关闭通过取消令牌立即中断等待。配置热加载会按旧速率先结算再切换 |
| 配置 | `FastTunnel:Bandwidth`、`FastTunnel:Ops`；`Ops:PublicEndpoints=false` 时 `/healthz` 与 `/metrics` 要求 JWT |

### 设计取舍与残余风险

1. **软限速**：令牌不足时延迟写入，靠 Pipe 背压回传发送方；不是内核级 QoS，突发窗口内仍会超过平均速率。
2. **单机计量**：`TrafficMeter` 是进程内累计，重启清零、多实例不会聚合；Prometheus 侧用 `rate()` 计算速率，跨实例需在抓取端按 `instance` 求和。
3. **`/metrics` 默认匿名**：会暴露已映射域名与主体 Id。生产应在网关层限制来源 IP，或设 `Ops:PublicEndpoints=false` / `IncludeClientLabels=false`。
4. **端点可达性**：请求 Host 若命中 `WebList` 会被转发到内网，`/healthz`、`/metrics` 必须用服务端自身地址 + `BinPort` 访问，或由网关按路径固定路由。
5. **`Throttle` 级别仍未接限速动作**：`BanLevel.Throttle` 只建模 / 记录，不会自动调低带宽上限；如需联动，应在 `BanService` 里调用 `Bandwidth` 运行时覆盖（当前只支持配置静态规则）。
6. **端口转发仍默认关闭**（`EnableForward=false`）：计量覆盖它是为了显式开启后不留盲区，不代表该路径变成可内容审计。

**仍未实施**：S3 旁路送审 / 主动巡检（法务前置）、正式受信任证书、实名 / 账号体系与凭据轮换、DNS TXT / ICP 备案校验、上报主管部门与申诉通道流程对接（S4）、Dashboard / Admin UI、`Throttle` 级别自动降速、`PROXY protocol`（已确定不做，XFF 覆盖）。

**验证**：`dotnet build FastTunnel.sln` 0 错误；`dotnet test FastTunnel.Core.Tests` **307/307 通过**。本轮新增 `ClientTokenDirectoryTests` / `TrafficMeterTests` / `OpsControllerTests` / `SubjectBindingTests` 共 27 例，并扩充 `ServiceRegistrationTests`。覆盖：主体解析与重复检测、配置热加载、停用条目不退回无鉴权、域名 / 子域授权、单会话原子顶替、按 account 熔断精确命中、两条 Pipe 写入方式不重复计数、双向流计量、速率优先级、令牌桶等待时长、Prometheus 标签转义与匿名开关、DI 容器解析。

## 附录 C.5 · 多格式配置文件（2026-11-14）

> 基础设施改进，与合规主线无直接关系，但上一轮引入的 `Clients` / `Bandwidth` / `Abuse` /
> `Ops` 配置项较多，为部署方提供 JSON 之外的 INI 选择，降低手写 JSON 出错率。
> **决策记录**：最初实现为 JSON + YAML + TOML + INI（引入 YamlDotNet / Tomlyn 两个第三方包），
> 按用户「零新增第三方依赖优先」的要求收敛为 **JSON + INI**，YAML / TOML 的 Source/Provider
> 与两个包引用已移除；INI 由共享框架自带 Provider 承担，无需任何新依赖。

| 项 | 落地内容 |
| --- | --- |
| 加载入口 | `FastTunnel.Core/Configuration/FastTunnelConfigBuilderExtensions.AddFastTunnelConfigFiles(configDirectory, env, includeJson)`：按 `json → ini` 顺序加载所有实际存在的 `appsettings.{ext}`（后加载覆盖先加载），随后同序追加 `appsettings.{env}.{ext}` 环境变体；全部 `reloadOnChange` |
| INI | 共享框架自带 `IniConfigurationProvider`；无原生数组，需列表时用节名下标 `[FastTunnel:Clients:0]` |
| 服务端接线 | `FastTunnel.Server/Program.cs`：`config/` 目录，任一基础格式存在即可（全缺失抛 `FileNotFoundException`，保持原 `optional:false` 语义）；Windows 服务下文件存在性用 builder 的 `IFileProvider` 判定，不用 `File.Exists`（CWD=System32 会判错） |
| 客户端接线 | `FastTunnel.Client/Program.cs`：`includeJson:false`（JSON 已由 `Host.CreateDefaultBuilder` 加载，避免重复注册），只补 `.ini` 变体，无扩展格式时不强制报错 |
| 示例 | `FastTunnel.Server/config/appsettings.sample.ini`、`FastTunnel.Client/appsettings.sample.ini`（`.sample` 命名避开自动加载）；4 份 README 已同步 |

**边界**：INI 不支持原生数组，复杂嵌套/列表结构建议留在 JSON、INI 只做少量覆盖；键重复时 INI Provider 会报错（JSON 允许覆盖）。

## 附录 C.6 · TCP/UDP 端口准入（2026-11-14）

> 对标 frp 的 `allowPorts` 与 `maxPortsPerClient`。与域名黑名单配对：HTTP 靠域名收口，
> TCP/UDP 靠端口收口——防客户端把恶意服务挂在 22/25/445 等知名端口，或抢占服务端自有监听。

| 项 | 落地内容 |
| --- | --- |
| 配置 | `FastTunnel:AllowPorts[]`（`{ Single }` 单端口 / `{ Start, End }` 闭区间，多条件取并集；空 = 不限制）与 `FastTunnel:MaxPortsPerClient`（去重端口数上限，0 = 不限制） |
| 校验 | `PortPolicy.IsPortAllowed`：先判 1–65535 合法性，再判白名单；条目非法（Start > End / 全 0 / null）被跳过，全部非法时拒绝并提示配置问题。白名单外拒绝的 reason 会通过控制通道日志回给客户端 |
| 上限判定 | `TunnelClient.CountForwardPorts(excludingPort)`：按会话内去重端口数计；同端口重新登记（顶替旧监听）通过 `excludingPort` 排除，不算新增 |
| 接线 | `LoginHandler.HandleLoginAsync` 转发循环：两项校验在启动监听之前完成，拒绝只跳过该条目并回日志，不影响同会话其它隧道；每次登录现读 `ServerOption.CurrentValue`，跟随配置热加载 |
| 测试 | `PortPolicyTests`（6 例：无配置不限、端口合法性、单值、区间、并集、非法条目）+ `ForwardAdmissionTests`（5 例：白名单外拒绝不启动监听、白名单内登记/退出释放、超上限拒绝、同端口顶替不计数、端口计数去重与排除） |

**边界**：白名单只作用于 TCP/UDP 转发登记，不影响 HTTP 子域名映射（那侧走域名黑名单与 `AllowedHosts`）；`MaxPortsPerClient` 对会话重登不算新增，但跨会话不共享配额（同一主体多会话时各算各的）。

## 附录 C.7 · 代理组负载均衡（2026-11-14）

> 对标 frp 的 `loadBalancer.group`。frp 差距清单第 2 项的地基改造：路由表从
> 「域名 → 单映射（后来者覆盖）」升级为「域名 → 组容器」，同组同口令同域名的
> 注册自动聚合，按请求随机分发。frp 差距清单据此可划掉"负载均衡"与"同域名单实例"两项。

| 项 | 落地内容 |
| --- | --- |
| 组容器 | `FastTunnel.Core/Server/WebHostGroup.cs`：host → 组；成员增删、随机选路（健康池优先，全不健康兜底随机）、被动摘除（连续失败出池、到期自动回池、成功清零） |
| 聚合规则 | 与 frp 一致——组名 + 组口令 + 域名完全一致才成组；口令不一致 / 组名不一致 / 有组 vs 无组一律拒绝入组，防止不相干客户端混入组劫持流量。无组注册保持旧版语义：同域名他人在线占用即拒绝 |
| 路由表 | `FastTunnelServer.WebList` 值类型改为 `WebHostGroup`；`TryRegisterWeb` 返回 reason；`RemoveWeb` 摘成员、组空时摘路由条目；`SnapshotWebs` 跨组拍平供封禁与运维 API 使用 |
| 选路 | `FastTunelProtocol` 解析 Host 时组内 `PickMember()`（每连接一次）；`WebInfo.GroupRef` 回引供转发链路上报成败 |
| 被动摘除 | `FastTunnel:LoadBalancer:FailureThreshold`（默认 3）/ `RecoverySeconds`（默认 30）。失败信号：回连路径客户端离线、mux 建流异常；成功信号：隧道建立。阈值 0 关闭 |
| 运维 API | `SystemController.GetAllWebList` 输出组名与组内成员数 |

**边界**：分发策略固定为随机（frp 文档同样只承诺随机，未暴露策略配置）；健康检查是服务端被动观测，不探测内网服务本身（frp 的主动探测跑在客户端侧，FastTunnel 客户端尚未实现）；多成员组的按 token 封禁只精确摘除肇事者的成员，host 级黑名单照常作用于整个域名。
