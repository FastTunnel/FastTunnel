# FastTunnel vs frp v0.72.0 本地 HTTP 穿透对照 — 阶段报告

> 状态：链路、压测工具、报告流水线已验证可用；**IIS:8080 与 hosts 尚未接入**（需要管理员执行一次
> `bench\exp\setup-iis-admin.ps1`），因此本页数字来自内置 Kestrel 源站（18080），
> 两侧共用同一源站，横向对比成立，但绝对数值不代表 IIS 场景。
> 拿到 `bench\out\exp\env.json` 后重跑同一条命令即可替换本页。

## 一、结果（256 并发容量档）

![对比图](chart.svg)

| 目标 RPS | FastTunnel 达成 | FastTunnel 错误率 | FastTunnel p99 | frp 达成 | frp 错误率 | frp p99 |
|---|---|---|---|---|---|---|
| 5000 | 4998 | 0% | 25.0ms | 3988 | 7.96% | 1641ms |
| 10000 | 9998 | 0% | 46.2ms | 2525 | 8.44% | 4287ms |
| 15000 | 14992 | 0% | 35.1ms | 3930 | 14.68% | 1122ms |
| 20000 | 19992 | 0% | 34.9ms | 3800 | 16.13% | 253ms |
| 25000 | 24856 | 0% | 35.2ms | 2519 | 25.65% | 2548ms |
| 30000 | 30050 | 0% | 18.1ms | — 中止（错误率超 20% 阈值） | — | — |

- FastTunnel：6 档全部达成目标速率、**0 错误**；服务端 CPU 0.5~2.7 核；30k RPS 时链路吞吐 ≈ 6.4 MiB/s。
- frp：从第一档起就有 8% 错误，达成速率与目标脱钩（2.5k~4.0k），p99 一度到 4.3 秒；
  历史运行中还出现过 `frpc` 因 `program exceeds 10000-thread limit` 直接退出。

## 二、frp 这组数据的性质：先排除"配错了"

低负载定向探测（4 连接 / 200 RPS / 600 请求）：

- 600 请求 **0 错误**、达成 199.9 RPS；
- frps 日志：`new work connection registered` × 10、`get work connection from pool` × 10；
- 即 **600 请求只用了 10 条 work connection**，keep-alive 复用正常。

结论：frp 的基础链路没问题，退化发生在**高并发**（256 条并发用户连接 + 高 RPS），
表现为错误率上升、达成速率与目标脱钩、`frpc` 线程暴涨。这不是"没关 TLS/日志"之类的配置问题
（两侧条件已对齐）。

## 三、测量口径说明（避免误读）

**1. 当前是"容量档"，不是"定速延迟档"。** 256 并发下 p50 几乎等于单连接两次请求的间隔：

| 目标 | 单连接节奏 256/R | 实测 p50 |
|---|---|---|
| 5000 | 51ms | 0.28ms（节奏远大于服务时间 → 真实服务延迟） |
| 10000 | 25.6ms | 24.2ms（排队等槽位） |
| 30000 | 8.5ms | 7.8ms |

要测"按目标速率发压时用户感受到的延迟"，用 `-AutoConnections`（连接数随档位放大到 target/50），
两档分开列。

**2. 发压机自身开销已修正。** 早期定速等待用「Sleep + 自旋」，1000 RPS 就白烧约 2 个核；
改成到点直接 Sleep 后：1000 RPS 自身 1.98 → **0.51 核**。每级结果都记 `SelfCores`，
可区分"隧道慢"还是"发压机先扛不住"。

## 四、待补（拿到管理员授权/机器空闲后）

```powershell
# 1) 以管理员部署 IIS:8080 + hosts（一条命令，可回滚）
powershell -ExecutionPolicy Bypass -File bench\exp\setup-iis-admin.ps1

# 2A) 容量档（256 并发）
powershell -ExecutionPolicy Bypass -File bench\exp\run-experiment.ps1

# 2B) 定速延迟档（连接数自适应）
powershell -ExecutionPolicy Bypass -File bench\exp\run-experiment.ps1 -AutoConnections

# 2C) frp 调平对照（预建 work connection，判断是否连接调度导致退化）
powershell -ExecutionPolicy Bypass -File bench\exp\run-experiment.ps1 -Frameworks frp -FrpPoolCount 64
```

## 五、复现方式

见 [README.md](README.md)：脚本、端口、配置项、指标口径、公平性设定全部列明；
`bench/out/exp/results.csv` 是每级的原始数据，`bench/out/exp/chart.svg` 由 `make-charts.ps1` 生成。
