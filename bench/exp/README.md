# FastTunnel vs frp v0.72.0 本地 HTTP 穿透对照实验

同一台机器、同一个源站、同一份压测工具，对比两条 HTTP 穿透链路的**延迟分布、吞吐、错误率**。

## 组成

| 角色 | FastTunnel 侧 | frp 侧 |
|---|---|---|
| 服务端 | `FastTunnel.Server`（发布版），BinPort 17270 | `frps` 0.72.0，bindPort 17000 |
| 客户端 | `FastTunnel.Client`，明文 ws 控制通道 + 明文 TCP mux | `frpc` 0.72.0，`transport.tls.enable=false` |
| 公网入口 | `http://<host>:17270`（按 Host 头路由） | `http://<host>:17080`（vhostHTTPPort） |
| 公网域名 | `bench.ftbench.net` | `bench.frpbench.net` |
| 源站 | `127.0.0.1:8080`（IIS）或内置 Kestrel 源站 18080 | 同左 |

- 源站页面：`bench/exp/www/index.html`（约 1KB，无外部资源）。
- 本地 DNS：`setup-iis-admin.ps1` 会往 hosts 追加 `bench.ftbench.net`、`bench.frpbench.net → 127.0.0.1`。
- 压测：`bench/LoadRunner`，开环定速（每个请求领取全局槽位），逐请求记录延迟；
  一条连接一个线程 + 阻塞 I/O，压测工具自身不产生每请求分配。

## 公平性设定

- 两侧都**关闭 TLS**（fasttunnel: `Tls.Enabled=false` + `Protocol=ws`；frp: `transport.tls.enable=false`）。
- 两侧日志都降到最低（fasttunnel: Serilog `Fatal`；frp: `log.level=error`、`log.to=disable`）。
  排查链路时可用 `-LogLevel Debug` 重跑，正式数据一律用 `Fatal`。
- 相同源站、相同 Host 头、相同 keep-alive 连接数、相同 RPS 阶梯。
- fasttunnel 走 mux（一条长连接承载所有转发），frp 走默认 tcpMux。

## 步骤

### 1. 源站（二选一）

**A. 真 IIS:8080（推荐，需要管理员）**

```powershell
# 以管理员身份运行：在 8080 上部署 bench\exp\www、追加两条 hosts 记录，并冒烟验证
powershell -ExecutionPolicy Bypass -File bench\exp\setup-iis-admin.ps1
# 结束实验后卸载
powershell -ExecutionPolicy Bypass -File bench\exp\teardown-iis-admin.ps1
```

脚本会写 `bench/out/exp/env.json` 记录实际生效的源站端口/路径（8080 被别的站点占用时改加虚拟目录，端口空闲则新建站点）。

**B. 内置 Kestrel 源站（无需管理员）**

不生成 `env.json` 即可：`run-experiment.ps1` 会自动在 18080 拉起同内容源站。

### 2. 跑实验

```powershell
# 默认：5000/10000/15000/20000/25000/30000 RPS，256 连接，预热 3s + 测量 9s
powershell -ExecutionPolicy Bypass -File bench\exp\run-experiment.ps1

# 只压一个框架 / 指定阶梯
powershell -ExecutionPolicy Bypass -File bench\exp\run-experiment.ps1 -Frameworks ft -LevelsCsv 5000,10000
```

全程无弹窗（所有子进程 `-WindowStyle Hidden`），结束后自动清理进程。

### 3. 产物

- `bench/out/exp/results/{ft,frp}-rps*.json`：每级原始数据（含 p50/p90/p99/p99.9/max、错误数、达成速率、进程 CPU）
- `bench/out/exp/results.csv`：汇总
- `bench/out/exp/report.md`：报告（含同档位对比表）

## 指标口径

| 指标 | 含义 |
|---|---|
| 达成 RPS | 测量窗口内实际完成（收到完整 200 响应）的请求数 / 窗口秒数 |
| ErrorRate | 非 200 响应 + 连接异常 + 超时的占比 |
| LateSendRatio | 发送时刻晚于计划 5ms 以上的请求占比：机器扛不住时的直接信号 |
| p50/p90/p99/p99.9 | 从写出请求到读完响应体的端到端延迟分位 |
| MiB/s | 响应体吞吐 |
| 服务端/客户端核 | 被测进程在该级的平均 CPU 核数（`TotalProcessorTime` 差值 / 时长） |

## 已知注意点

- 单级结论受机器状态影响；对比必须同一次运行内交替进行（脚本按框架顺序整轮跑完）。
- frp 的 HTTP vhost 在 **256 并发 + 高 RPS** 下观测到大量连接重建与错误（frpc 曾因线程耗尽退出）；
  低并发（4 连接 / 200 RPS）下 600 请求 0 错误、仅 10 条 work connection，说明基础链路正常，
  问题出在高并发下的连接调度。报告里会把两个并发档位分开列。
- 内置 Kestrel 源站与 IIS 的绝对性能不同，但**两侧共用同一源站**，横向对比仍然成立。
## 附：手动配 IIS（不想跑脚本时）

当前这台机器的 8080 由 HTTP.sys/IIS 监听，但请求 `http://127.0.0.1:8080/index.html` 返回 404，
说明我们的页面还没部署上去。手动三步：

1. **IIS 管理器** → 网站：如果已经有站点占着 8080，就在该站点上右键「添加虚拟目录」，
   别名 `bench`，物理路径 `G:\Github\FastTunnel\bench\exp\www`；
   如果 8080 空闲，就「添加网站」：名称 `FastTunnelBench`、物理路径同上、端口 8080。
2. 用**管理员记事本**打开 `C:\Windows\System32\drivers\etc\hosts`，追加两行：
   ```
   127.0.0.1	bench.ftbench.net
   127.0.0.1	bench.frpbench.net
   ```
3. 浏览器/curl 验证：`http://127.0.0.1:8080/index.html`（虚拟目录则是 `/bench/index.html`）返回 200。

然后告诉我「IIS 好了」或直接说「跑」，我会自动探测 8080 上的 `/index.html` 与 `/bench/index.html`
并生成 `env.json`，再跑最终压测。
