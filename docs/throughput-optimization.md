# 吞吐优化记录（多路复用数据面）

本记录对应把「一条 TCP 承载所有转发」的多路复用数据面做的一轮吞吐优化：先定位瓶颈、再改代码、最后用可复现的基准对比前后。

## 一、结论

同一台机器、同一份基准脚本，**同一时段交替 A/B**（各项至少 2 轮，取中位）对比：

| 场景 | 指标 | 改动前 | 改动后 | 变化 |
|---|---|---|---|---|
| Mux 数据面（同进程 loopback TCP，双向往返，64KB 写） | 单向吞吐 | 785~923 MiB/s（中位 ~845） | 1309~1384 MiB/s（中位 ~1333） | **+58%** |
| 同上 | CPU | 4.5~5.1 核 | 3.2~4.5 核 | -15%~-20% |
| 同上 | 每 GiB 分配 | 62.8 MiB | 22.8 MiB | **-64%** |
| 端到端（Server+Client 两进程 + 内网回显），1 条流 | 单向吞吐 | 131.7~132.5 MiB/s | 140.6~146.2 MiB/s | **+9%** |
| 同上 | 服务端 CPU | 1.30~1.37 核 | 0.75~0.89 核 | **-38%** |
| 同上 | 客户端 CPU | 0.99~1.00 核 | 0.60~0.63 核 | **-39%** |
| 端到端，8 条并发流 | 单向吞吐 | 321~323 MiB/s | 676~718 MiB/s | **2.1x** |
| 同上 | 服务端 CPU | 3.6~4.0 核 | 4.5~4.8 核 | 每字节成本约降 40% |

一句话：**单位字节的 CPU 成本降到原来的约 60%，多流并发吞吐翻倍（2.1x），单流吞吐 +9%；单流仍是「往返延迟」而不是「每字节成本」主导（见第六节）。**
另：建流速率（短连接请求-应答）从 19 976 流/秒 / 8.6KB 每连接，提升到 21 878 流/秒 / 6.8KB 每连接（非同时段，仅量级参考）。

注意测量口径：单流是「64KB 请求-应答」型负载，吞吐 = 64KB / 往返延迟，**绝对数字会随机器状态明显漂移**（同一份代码不同时段实测 130~240 MiB/s 都出现过）。所以上表的单流结论只认「同一时段交替跑」的那一组；早期把不同时段的 206 与 237 直接相比是错的，已按同时段 A/B 更正。

## 二、找到的瓶颈与改法

按收益从大到小。行号指改动前的代码。

### 1. 窗口回补：每 64KB 一次 `Task.Run` + 数组分配 + 一次内核写（`MuxStream.OnReceiveConsumed` / `MuxSession.TrySendWindowUpdate`）

旧实现每消费 64KB 就 `_ = Task.Run(...)` 一个新任务，任务里 `new byte[4]`、抢一次写锁、写 13 字节、flush 一次。1 GiB 流量就是 1.6 万次线程池投递 + 1.6 万次内核写 + 1.6 万个短命对象。

改成：无锁 `Interlocked` 累计 → 到 128KB 才回补一次；同一时刻最多一个在途回补任务（拥塞时自动合并增量）；帧头 + 4 字节负载直接写进传输管道（零分配）；会话关闭时静默丢弃。回补帧数降到 1/2，且不再有线程池投递与分配。

### 2. 读取侧：一帧一次 `ReadAsync` + `AdvanceTo`，丢弃帧还要租数组（`MuxSession.RunAsync` / `ReadExactAsync`）

旧实现每帧至少两次 `PipeReader.ReadAsync`（帧头、帧体），每次都要 `AdvanceTo`，遇到已关闭的流还要从 `ArrayPool` 租数组把帧体「读掉」。

改成：一次 `ReadAsync` 拿到多少就解多少帧（`SequenceReader` 解帧头，跨段也安全），帧体直接用管道缓冲的切片交给目标流；丢弃帧只移动位置、不读不租。

### 3. 写入侧：每帧一次写锁 + 一次 flush（`MuxSession.WriteFrameAsync`）

旧实现一帧一锁一 flush，1 GiB / 16KB = 6.5 万次写锁 + 6.5 万次 flush，等于每 16KB 一次内核写，还让所有流在锁上排队。

改成：一批数据（最多 256KB）只取一次写锁，切成若干 16KB 帧写进同一段传输缓冲，**只 flush 一次**；帧头与负载写进同一次 `GetSpan` 申请到的连续内存，整帧一次拷贝。

### 4. 发送侧每流一条中间管道 + 后台 SendLoop（`MuxStream._send` / `SendLoopAsync`）

旧实现每个方向的写入先落进每流的发送管道（第一次拷贝），后台任务再读出来写进传输管道（第二次拷贝），中间还多一次线程跳转，每条流多一个常驻 Task 和最多 256KB 缓冲。

改成：`MuxStream.Output` 是直接落到会话传输管道的 `PipeWriter`——写入按窗口额度切帧后一次 flush，**只有一次拷贝、没有中间任务**；反压由窗口额度提供（窗口用尽就地等待），每流常驻内存也随之下降。

### 5. 数据泵按 4KB 搬（`ForwardDispatcher.PumpSocketToPipeAsync` / `SwapHandler.BridgeAsync`）

`PipeWriter.GetMemory()` 不带 sizeHint 时只返回当前段剩余的几 KB，于是 64KB 数据被拆成十几次「读 → 帧 → 内核写」。实测这一项在端到端链路上就值一倍吞吐。

改成：显式申请 64KB；并且只要对端还有数据可读（`Socket.Available` / `NetworkStream.DataAvailable`）就继续读满一块再写一次管道——批大了，但交互型流量在对端没数据时立刻 flush，延迟不受影响。

### 6. 客户端桥接走 `Stream.CopyToAsync`（`SwapHandler.BridgeAsync`）

`Stream.CopyToAsync` 会从 `ArrayPool` 租 80KB 中转缓冲，读一次、拷一次、写一次。

改成：`MuxStream` 本身就是 `IDuplexPipe`，下行用 `PipeReader.CopyToAsync(Stream)`（直接用管道段写 socket），上行用泵直接写进管道——省掉 80KB 中转缓冲与一次全量拷贝。

### 7. 计量/限速热路径每次写入都有分配与多余查表（`TrafficMeter.AccountAsync`）

旧实现每次写入：`ConcurrentDictionary.GetOrAdd` 传捕获了 `scope` 的闭包（**每次写都分配一个闭包对象**）、对空规则表也算字符串哈希查三次、`TokenBucket` 先 `Configure` 再 `Reserve` 拿两次锁。

改成：`static` 委托去掉闭包；规则表为空时只比 `Count`；速率解析结果按「规则集 + 主体」缓存；令牌桶一次加锁完成热加载检查与预占；不限速路径完全不进异步状态机。

### 8. 每条转发连接一次字符串插值日志（`ForwardDispatcher`）

`logger.LogDebug($"...")` 在日志级别未开启时依然会做插值、生成字符串。改为 `IsEnabled` 保护 + 结构化参数。

### 9. 客户端用工作站 GC（`FastTunnel.Client.csproj`）

客户端是长跑数据面，工作站 GC 的 gen0 回收在压测里是可见的抖动源。与服务端对齐改为 `ServerGarbageCollection`。

### 10. 单流窗口 256KB → 1MB（`MuxFrame.InitialWindow`）

单流吞吐上限 ≈ 窗口 / RTT：256KB 在 50ms RTT 的链路上只能跑到 ~5MB/s，跨境/跨地区的大文件传输会被这条硬顶住。抬到 1MB 后同链路约 20MB/s，代价只是「正在传输的流最多缓存 1MB」（空闲的 keep-alive 连接不预分配）。

## 三、顺带修掉的 Bug

- **窗口更新帧负载跨段会让整条隧道断开**：4 字节负载正好跨在两个管道段上时，旧代码直接对 `FirstSpan` 读 4 字节抛 `ArgumentOutOfRangeException`，异常冒到会话主循环 → 整条多路复用连接（连同上面所有转发连接）关闭。端到端压测（8 并发 × 4GiB）复现，已修复并补了回归测试 `窗口更新帧的4字节负载跨段时不会拖垮会话`。
- **丢帧路径的两次搬运**：已关闭流的数据帧不再「先租数组读出来再丢掉」。

## 四、兼容性

- **帧格式与单帧上限不变**（9 字节头、16KB 负载），新旧对端可以互通；吞吐靠「多帧一次 flush」而不是放大单帧。
- **窗口回补阈值固定 128KB**，小于旧实现给每条流的初始信用（256KB），因此新接收端 + 旧发送端不会出现「旧发送端发满信用后等不到更新」的死锁；`MuxFrameTests.窗口常量之间必须自洽` 断言了这个约束。
- **单流初始窗口 1MB**：对旧对端是「多给的信用」，只影响「新发送端 + 新接收端」组合的实际吞吐。

## 五、复现方法

基准代码在 `bench/`（不在解决方案里，避免影响正常构建）：

```powershell
# 1) 数据面微基准：两端 MuxSession 跑在真实 loopback TCP 上，对端回显
dotnet run -c Release --project bench/FastTunnel.Bench -- mux 2048 64 3

# 2) 建流速率
dotnet run -c Release --project bench/FastTunnel.Bench -- conn 30000 64

# 3) 端到端：真实 Server + Client 两个进程 + 内网回显，含逐字节校验
powershell -File bench/run-e2e.ps1 -TotalMiB 2048 -Connections 8

# 4) 反复对比用（起停隧道并跑多组并发）
powershell -File bench/measure.ps1 -Publish -TotalMiB 1024 -Connections 8
```

说明：

- `mux` 模式统计吞吐、进程 CPU、`GC.GetTotalAllocatedBytes` 与 GC 次数，同一进程内两端对称，适合比较改动前后。
- 端到端模式会按确定性字节图案逐字节校验回显内容，任何「丢一段 / 踩内存」都会直接报错，不会被吞吐数字掩盖。
- HTTP 路径另外做过冒烟：配一条 `Webs` 映射，用 `curl -H "Host: <子域>.<根域>"` 下载 4MB 文件，SHA-256 与源文件一致（验证响应体没有被优雅关闭截断），隧道内耗时约 10ms。

## 六、还能继续挖的点（按预期收益排序）

1. **MUX 承载连接目前走 Kestrel 的连接层**：每次 flush 都要经 Kestrel 的输出管道与 IOQueue 调度。把 MUX 承载从 Kestrel 手里接管（自定义 `ConnectionHandler`/裸 socket 上直接跑 `MuxSession`）可以再去掉一跳调度，预期单流吞吐还有 20%~30%。
2. **跨流合批**：目前是一批一锁一 flush（同一条流内合批）。若让小包场景的多条流合成一次内核写，可再省一批系统调用，但需要多一次拷贝，收益取决于小包占比。
3. **帧长协商到 64KB**：需要握手协商以避免打到只认 16KB 的旧对端，可再降一半帧处理开销。
4. **每客户端多会话**：单会话的读取循环与写锁是天然串行点，按 CPU 分片成多条 mux 连接能进一步提高多核利用率（实现与运维复杂度都会上升）。

## 七、第二轮：三条数据面的低拷贝补齐

这一轮不改线格式，只修「看起来零拷贝、实际没走到」和「每字节多搬一次」的问题。

### 1. 生产配置下的 UDP 管道路径此前是死代码

`ForwardDispatcher.RequestSwapAsync` 返回的 UDP 流在生产 DI 中一定被 `TrafficMeter.WrapBidirectional` 包一层 `MeteredStream`；
而旧代码用 `_tunnelStream is IDuplexPipe` 判断，因此 `UdpProxyListener` / `SwapHandler` 的管道切片读路径**从未命中**。
现在 `MeteredStream` 在底层流是 `IDuplexPipe` 时透出计量后的 `Input`/`Output`，UDP 回包重新走单段直切。

### 2. TCP / HTTP 的 socket → MUX 少一次整块 memcpy

`PipePumps` 原来租 64KB 数组，socket 读进数组后再 `WriteAsync` 到目标管道。
现在对 `MuxStreamWriter`（含计量包装）直接 `GetMemory → socket.ReceiveAsync → Advance → Flush`，
省掉一次 64KB 全量拷贝；Kestrel 等可能被并发完成并还池的 writer 仍走保守路径。

### 3. UDP 数据报直接写进 MUX 帧

新增 `IDatagramWriter`，`MuxStream` / `DuplexPipeStream` / `MeteredStream` 都实现它。
`UdpDatagramFramer` 优先走这条路径：长度前缀和负载在写锁内一起写进传输缓冲，
不再先租 `[长度+负载]` 临时数组再整包拷一遍。32KB 数据报跨两个 MUX 帧仍保持连续 UDP 线格式。

### 4. 计量热路径缓存代理计数器

`MeteredPipeWriter` / `MeteredStream` 在创建时解析一次 `ProxyCounters`，
每次写入不再做 `ConcurrentDictionary` 查找和主体字符串比较。

### 5. 高频日志停止无谓插值

HTTP / TCP / UDP 的每请求 `LogDebug($"...")` 改为 `IsEnabled` 保护 + 结构化参数。

### 验证

- `dotnet test`：**346/346 通过**（新增跨 MUX 帧 UDP 数据报、直写 MuxWriter 两条回归测试）。
- `dotnet build FastTunnel.sln -c Release`：**0 错误**。
- 真实端到端：Server + Client + 本地 UDP echo，小包与 20KB 大包均收到 `ok`，日志无异常。
- `bench/FastTunnel.Bench -- mux 512 64 3`：当前机器单向 740~769 MiB/s；该基准不经过 `PipePumps`，
  只作为当前机器状态参考，不能与上一节不同时段的数字直接比较。

### 仍未做、收益/风险更高的点

1. UDP 按会话队列合批：多个小数据报共用一次 flush，需要再付一次队列拷贝，必须 A/B 后决定。
2. MUX 承载脱离 Kestrel 连接层，直接跑在裸 socket / `IConnectionListener` 上，预计还能省一跳调度。
3. 帧长从 16KB 协商到 64KB，需要握手协商以兼容旧对端。
4. `OpenStreamAsync` 的 SYN JSON 序列化仍每条连接分配一个 `byte[]`，可改池化写入。
