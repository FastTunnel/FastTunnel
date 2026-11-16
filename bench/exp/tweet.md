# 推文草稿（配 `bench/exp/tweet-chart.png`）

> X/Twitter 对中文按 2 字符计，以下三条都在 280 以内，可直接复制。
> 都建议保留"单机 loopback 对照"这句，避免被当成公网基准。

## 版本 A：结论优先（推荐）

```
给 FastTunnel 做了轮穿透性能对照：本机 IIS 静态页，256 并发，两侧明文+关日志。

HTTP 穿透 10K RPS：FastTunnel 9992 RPS / 0 错误 / p99 5ms；frp 0.72 的 type=http 只有 7781 / 2.8% / 397ms。

上限：FastTunnel ~43K RPS，frp(tcp) ~34K，内存 frp 高 1.3 倍。单机 loopback，仅供参考。
```

## 版本 B：澄清 "Go 不该这么慢"

```
澄清一下"Go 不该这么慢"：

① frp 默认已开 mux；
② poolCount 默认 5，压 256 并发必须调大，调平后 10K 档 1435/84% 错误 → 4941/0.58%；
③ 换 type=tcp 后 10K 是 9997 RPS / p99 7.6ms。

所以慢的是 http vhost：5 秒 12510 请求，新建 12891 条 work connection。
```

## 版本 C：数据流

```
FastTunnel vs frp 0.72，同源站同压测，交替跑：

5K   → 4973 vs 4978 RPS，均 0 错误
10K  → 9975 vs 9953，全程 0 错误
30K  → 29907 vs 29995
60K  → 42131 vs 32132
90K  → 42862 vs 33783  ← 上限差 ~27%

内存合计 frp 高 1.25~1.4 倍；错误率两边都是 0。
```

## 建议的附图与标签

- 附图：`bench/exp/tweet-chart.png`（1200×800，柱状=达成 RPS，折线=p99）
- 标签：`#FastTunnel #frp #内网穿透 #性能测试 #dotnet #golang`

## 发之前请确认（避免被挑刺）

1. 注明**单机 loopback**、源站是 **1KB 静态页**、frp 为 **0.72.0 官方二进制**。
2. 提到 frp 时区分 **http 模式** 与 **tcp 模式**，并写明 `poolCount` 已调平到并发数。
3. 不要写"frp 只能跑 1.4K"——那是默认配置下的结果，调平后是 4941（http）/ 9997（tcp）。
4. 内存口径写"进程私有字节"，不是 RSS 峰值。
