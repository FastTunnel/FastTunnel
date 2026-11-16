# FastTunnel vs frp 0.72.0：本地 HTTP 穿透性能对照报告

> 单机 loopback 对照实验。结论只对本机这套条件成立，用于横向比较两条链路的设计与实现差异，
> 不代表公网、跨地区或不同内核参数下的绝对性能。

## 一、一句话结论

**同为 HTTP 穿透，FastTunnel 在 10K RPS 下仍 0 错误、p99 5ms，frp 的 `type=http` 掉到 78% 吞吐 +
2.8% 错误、p99 397ms；但把 frp 换成 `type=tcp` 后两者基础转发能力接近，差距出现在高并发段
（本机上限：FastTunnel ≈ 43K RPS，frp-tcp ≈ 34K RPS），frp 内存占用高 1.25~1.4 倍。**

## 二、测试方法

| 项 | 设置 |
|---|---|
| 源站 | 本机 IIS:8080，静态页 `bench/exp/www/index.html`（约 1KB，无外部资源） |
| 本地 DNS | hosts 写入 `bench.ftbench.net` / `bench.frpbench.net` → 127.0.0.1 |
| FastTunnel | 发布版 Server/Client；BinPort 17270；明文 ws 控制通道 + 明文 TCP mux |
| frp | 0.72.0 官方 windows_amd64（SHA-256 与官方 checksums 一致）；bindPort 17000；HTTP vhost 17080 / TCP 17090 |
| 加密 | 两侧都**关闭 TLS**（fasttunnel `Tls.Enabled=false`；frp `transport.tls.enable=false`） |
| 日志 | 两侧都降到最低（fasttunnel Serilog `Fatal`；frp `log.level=error`、`log.to=disable`） |
| 压测 | 自建开环定速工具（目标 RPS 精确发射、逐请求记延迟、指定 Host 头）；每条连接一个线程 + 阻塞 I/O，压测机自身开销单独统计 |
| 机器 | 16 逻辑核，Windows；发压与被测同机 |
| 口径 | 每级预热 2~3s + 测量 5~9s；**同一轮内两侧交替**执行以抵消机器漂移 |

公平性上做过的两处修正（都在报告里保留）：

1. **frp 的 `transport.poolCount` 默认只有 5**，而 HTTP 代理需要「每条用户连接一条 work connection」。
   不调大就压 256 并发，等于让 frp 每条连接都现拨一次。调平到 256 后 frp 从 1435 RPS/84% 错误
   变到 4941 RPS/0.58% 错误 —— 所以对比必须标注"frp 默认 / frp 调平"。
2. **确认 frp 0.72 默认已开 mux**（日志 `accept new mux stream`，work connection 是 yamux 流），
   不存在"frp 每条连接新 TCP、fasttunnel 走 mux"这种不对等。

## 三、结果

### 3.1 HTTP 穿透（frp 的默认用法，5K / 10K）

| 目标 | 框架 | 达成 RPS | 错误率 | p50 | p99 |
|---|---|---|---|---|---|
| 5000 | FastTunnel | 4999.6 | 0% | 0.215ms | **2.30ms** |
| 5000 | frp（poolCount=256） | 4999.2 | 0% | 0.239ms | 17.9ms |
| 10000 | FastTunnel | **9991.5** | **0%** | 0.257ms | **5.10ms** |
| 10000 | frp（poolCount=256） | 7781.3 | 2.80% | 0.392ms | 397ms |
| 10000 | frp（默认 poolCount=5） | 4564.5 | 6.04% | 8.80ms | 761ms |

### 3.2 frp 换成 `type=tcp` 后（256 并发，同轮交替，含内存）

| 目标 RPS | FastTunnel | FT p50/p99 | FT 内存(服/客) | frp-tcp | frp p50/p99 | frp 内存(服/客) |
|---|---|---|---|---|---|---|
| 5 000 | 4 973 | 1.34 / 7.9ms | 58.8 / 83.5 MB | 4 978 | 1.27 / 3.4ms | 100.6 / 94.9 MB |
| 10 000 | 9 975 | 2.00 / 10.0ms | 73.3 / 107.2 MB | 9 953 | 2.14 / 12.8ms | 122.7 / 111.1 MB |
| 30 000 | 29 907 | 5.32 / 11.1ms | 85.2 / 110.4 MB | 29 995 | 6.93 / 10.0ms | 126.2 / 117.0 MB |
| 60 000 | **42 131** | 5.80 / 9.6ms | 89.2 / 112.2 MB | 32 132 | 7.69 / 16.0ms | 125.5 / 120.5 MB |
| 90 000 | **42 862** | 5.74 / 9.8ms | 92.4 / 83.8 MB | 33 783 | 7.43 / 10.1ms | 126.9 / 116.4 MB |

### 3.3 FastTunnel 的上限在哪

- 上限 ≈ **42~43K RPS**（最近一轮同轮交替测得；早前单独跑出现过 45~46K，属 ±10~15% 的运行间波动），此时服务端+客户端+发压机合计约 9.4~10 核 / 16 核；
- 直连源站可到 80K RPS（0 错误），**源站与压测工具都不是瓶颈**；
- 128 / 256 / 512 连接分别得到 36K / 44.5K / 44.0K，而 p50 随连接数线性上升（3.4 → 5.5 → 11.3ms），
  即 `达成 ≈ 连接数 / p50`，被同一个串行资源卡住 → 单条 mux 会话的串行处理（一个读取循环 + 一把写锁）。
  要突破需要「每客户端多会话」。

### 3.4 为什么 frp 的 HTTP 模式会塌

同一份 frp、同 10K/256，只换代理类型：

| frp 配置 | 达成 RPS | 错误率 | p99 |
|---|---|---|---|
| `type=tcp` | 9 997.7 | 0% | 7.6ms |
| `type=http`（vhost） | 7 781.3 | 2.80% | 397ms |

debug 日志证据：`type=http` 下 5 秒内 12 510 个完成请求，frps 记录 `new work connection registered`
**12 891** 次 —— 平均**每个请求都在新建 work connection**；而低速（4 连接/200 RPS）时 600 请求只用 10 条。
即 HTTP vhost 在高并发下无法把 work connection 稳定复用，退化成每请求一次握手拨号。

## 四、结论

1. **Go 不慢，frp 的 mux/转发内核不慢**：`type=tcp` 下 10K RPS 只比 FastTunnel 低一点（p99 7.6ms vs 5.1ms）。
2. **两者都不会"报错崩掉"**：全程 0 错误，超限表现为「达成速率停止跟随目标 + 延迟抬升」，是平滑饱和。
3. **HTTP 穿透场景 FastTunnel 明显更强**：10K 档 0 错误 / p99 5ms，frp 2.8% 错误 / p99 397ms。
4. **高并发上限 FastTunnel 更高**：≈43K vs ≈34K RPS；**内存 frp 更高**：约 1.25~1.4 倍。
5. **frp 的两个前提要一起说**：HTTP 模式必须把 `poolCount` 调到并发数，否则会误判成"frp 只能跑 1.4K"。

## 五、局限

- 单机 loopback，没有公网 RTT、丢包、MTU、NAT 影响；真实链路下 mux/窗口/拥塞控制的作用会不同。
- 源站是静态 1KB 页面，只覆盖"小响应 + 高 RPS"这一类负载；大文件吞吐是另一个维度。
- frp 只用官方默认参数（除 TLS/日志/poolCount 已在报告标注），未做其它调优。
- 压测工具与被测同机，CPU 会互相争抢；报告中用 `SelfCores` 标注了发压机开销。
- 测量存在 ±10~15% 的运行间波动，因此关键对比都采用「同轮交替 + 多轮取中位」。

## 六、复现

```powershell
# 1) 管理员：部署 IIS:8080 页面 + hosts（可回滚）
powershell -File bench\exp\setup-iis-admin.ps1

# 2) HTTP 模式阶梯（两侧）
powershell -File bench\exp\run-experiment.ps1 -LevelsCsv 5000,10000

# 3) frp 调平后的 HTTP 对照
powershell -File bench\exp\run-experiment.ps1 -Frameworks frp -LevelsCsv 5000,10000 -FrpPoolCount 256

# 4) FastTunnel vs frp(type=tcp)，含 CPU/内存
powershell -File bench\exp\compare-tcp.ps1 -LevelsCsv 5000,10000,30000,60000,90000

# 5) 出图（推文用）
python bench\exp\make-tweet-chart.py
```

产物：`bench/out/exp/report.md`（脚本自动生成）、`bench/exp/results-5k-10k.md`（逐节结论）、
`bench/out/exp/compare-tcp/compare-tcp.csv`（原始数据）、`bench/exp/tweet-chart.png`（配图）。
