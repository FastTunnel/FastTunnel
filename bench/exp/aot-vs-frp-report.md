# FastTunnel 3.6.0 NativeAOT vs frp 0.72.0（2026-11-17）

## 口径
- 对比：FastTunnel 3.6.0 AOT（self-contained 单 exe，win-x64）vs frp 0.72.0 `type=tcp`。
- 产物：`bench/out/aot-v360-{server,client}`（3.6.0 源码编译，含 source-gen/HostingStartup 修复）。
- HTTP 穿透链路：压测器 → 隧道服务端 → 客户端 → IIS 源站（8080）；两侧同轮交替，2 轮。
- 阶梯 RPS：5k / 10k / 30k / 60k / 90k。压测 LoadRunner 相同。
- AOT 配置注意：单文件需 `Serilog:Using = ["Serilog.Sinks.Console"]`（File sink 被裁剪，列进去会 FileNotFoundException）。脚本已自动生成配置副本。
- 数据：`bench/out/exp/aot-vs-frp-aot-v360/aot-vs-frp.{csv,json}`，日志 `bench/out/exp/aot-vs-frp-run.log`。

## 稳态数据（2 轮均值；饱和段取 30k~90k）

| 目标 RPS | 达成 RPS (AOT / frp) | 错误率 | p50 ms (AOT / frp) | p99 ms (AOT / frp) | CPU 核 (AOT / frp) |
|---|---|---|---|---|---|
| 5000 | 4979 / 4989 | 0% / 0% | 1.42 / 1.95 | 3.71 / 4.43 | 1.09 / 1.55 |
| 10000 | 9971 / 9963 | 0% / 0% | 2.71 / 3.41 | 7.50 / 8.36 | 2.03 / 2.99 |
| 30000 | **27255 / 23153** | 0% / 0% | 9.37 / 11.13 | 10.36 / 12.95 | 5.58 / 5.89 |
| 60000 | **26717 / 22878** | 0% / 0% | 9.58 / 11.27 | 10.58 / 12.94 | 5.73 / 6.03 |
| 90000 | **25980 / 21998** | 0% / 0% | 9.80 / 11.67 | 11.04 / 13.54 | 5.56 / 5.90 |

（内存字段为进程私有字节合计，两轮间有爬升漂移，不作核心结论。）

## 结论
1. **低负载（5k/10k）**：两侧都能扛满目标、零错误；AOT 延迟更低（p50 约低 25~30%），CPU 约为 frp 的 2/3。
2. **饱和段（30k+）**：AOT 峰值吞吐 ~26~27k RPS，frp ~22~23k RPS，**AOT 高约 17~18%**；同时 p50/p99 更低、CPU 相当或略低。
3. **全程零错误**：frp 在本次（HTTP 穿透、交替压、tcp 模式）未复现历史 IL 对比里 10k 崩到 4564 RPS / 6% 错误的现象——本次没到那个并发模型，属不同负载形态，不能据此说 frp 已修好。
4. **与历史 IL 版对比的差异**：history 里 FastTunnel p50 低到 0.2ms 级；本次 AOT 版 p50 是 1.4~9.8ms 级。原因不是 AOT 变慢，而是两套实验的链路/压测模型不同（history 有独立的请求-响应延迟模型，本次是定速打满的 HTTP 穿透吞吐模型），**不可跨实验直接相减**。
5. **总评**：最新版本 AOT 版相对 frp 0.72.0（tcp）在吞吐上限、延迟、CPU 上均有稳定优势，且零运行时依赖。选型若看性能与部署简洁度，AOT 版更优；若需 frp 特有能力（UDP/STCP/P2P 等）则另论。

## 复现
```
powershell -File bench\exp\aot-vs-frp.ps1 -LevelsCsv 5000,10000,30000,60000,90000 -Rounds 2
```
