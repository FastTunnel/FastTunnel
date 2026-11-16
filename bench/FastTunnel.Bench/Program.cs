// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Mux;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastTunnel.Bench;

/// <summary>
/// 吞吐基准。两种口径：
/// <list type="bullet">
/// <item><c>mux</c>：两端 MuxSession 跑在真实 loopback TCP 上，对端做回显；
///   测「同一台机器、同一份代码」的数据面吞吐 / CPU / 分配，用于改动前后对比；</item>
/// <item><c>conn</c>：短连接往返，测每秒能建多少条流、每次建流的分配量。</item>
/// </list>
/// 用法：<c>dotnet run -c Release -- mux [MB] [chunkKB] [rounds]</c>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "mux";

        try
        {
            switch (mode)
            {
                case "mux":
                {
                    var mb = args.Length > 1 ? int.Parse(args[1]) : 512;
                    var chunkKb = args.Length > 2 ? int.Parse(args[2]) : 64;
                    var rounds = args.Length > 3 ? int.Parse(args[3]) : 3;
                    await RunMuxBenchAsync(mb, chunkKb * 1024, rounds);
                    return 0;
                }

                case "conn":
                {
                    var total = args.Length > 1 ? int.Parse(args[1]) : 20000;
                    var concurrency = args.Length > 2 ? int.Parse(args[2]) : 64;
                    await RunConnectionBenchAsync(total, concurrency);
                    return 0;
                }

                case "load":
                {
                    // load <remotePort> <echoPort> <totalMiB> <chunkKB> <connections>
                    var remotePort = int.Parse(args[1]);
                    var echoPort = int.Parse(args[2]);
                    var totalMb = int.Parse(args[3]);
                    var chunkKb = int.Parse(args[4]);
                    var connections = int.Parse(args[5]);
                    await RunEndToEndLoadAsync(remotePort, echoPort, totalMb, chunkKb * 1024, connections);
                    return 0;
                }

                default:
                    Console.WriteLine("mode: mux [MB] [chunkKB] [rounds] | conn [total] [concurrency] | load <remotePort> <echoPort> <totalMiB> <chunkKB> <connections>");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
    }

    // ---------------- mux 数据面吞吐 ----------------

    private static async Task RunMuxBenchAsync(int megabytes, int chunkBytes, int rounds)
    {
        var perRound = megabytes * 1024L * 1024L;

        await using var harness = await MuxHarness.CreateAsync();

        Console.WriteLine($"mode=mux payload={megabytes}MiB/direction chunk={chunkBytes / 1024}KiB rounds={rounds}");
        Console.WriteLine("round  seconds   one-way(MiB/s)  link(MiB/s)  cpu-cores  alloc(MiB)  gen0/1/2");

        for (var round = 1; round <= rounds; round++)
        {
            // 预热：第一轮把 JIT / 线程池 / 管道池都烧热
            var warm = round == 1;
            var bytes = warm ? perRound / 4 : perRound;

            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);

            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();

            await harness.TransferAsync(bytes, chunkBytes);

            sw.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

            var seconds = sw.Elapsed.TotalSeconds;
            var oneWay = bytes / 1024d / 1024d / seconds;
            var link = oneWay * 2;
            var cores = cpu.TotalSeconds / seconds;

            Console.WriteLine(string.Format(
                "{0,5}  {1,8:F2}  {2,13:F1}  {3,10:F1}  {4,9:F2}  {5,9:F1}  {6}/{7}/{8}{9}",
                round, seconds, oneWay, link, cores, alloc / 1024d / 1024d,
                GC.CollectionCount(0) - g0,
                GC.CollectionCount(1) - g1,
                GC.CollectionCount(2) - g2,
                warm ? "  (warmup)" : string.Empty));
        }
    }

    // ---------------- 建流速率 ----------------

    private static async Task RunConnectionBenchAsync(int total, int concurrency)
    {
        await using var harness = await MuxHarness.CreateAsync();

        // 预热
        await harness.RoundTripBatchAsync(Math.Min(concurrency * 2, total), concurrency, 1);

        Console.WriteLine($"mode=conn total={total} concurrency={concurrency}");
        Console.WriteLine("seconds   conn/s      alloc(MiB)  alloc/conn(B)");

        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var done = await harness.RoundTripBatchAsync(total, concurrency, 16);

        sw.Stop();
        var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        var seconds = sw.Elapsed.TotalSeconds;

        Console.WriteLine(string.Format(
            "{0,7:F2}  {1,9:F0}  {2,10:F1}  {3,13:F0}",
            seconds, done / seconds, alloc / 1024d / 1024d, (double)alloc / done));
    }

    // ---------------- 端到端：经真实 Server + Client 转发 ----------------

    /// <summary>
    /// 对「公网端口 → 服务端 → 多路复用 → 客户端 → 内网服务」整条链路压速。
    /// 内网服务由本进程自己提供（回显），因此一个进程即可产出端到端吞吐，
    /// 本进程自身的搬运开销在改动前后是常量，不影响对比。
    /// </summary>
    private static async Task RunEndToEndLoadAsync(
        int remotePort, int echoPort, int totalMegabytes, int chunkBytes, int connections)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var echo = new TcpListener(IPAddress.Loopback, echoPort);
        echo.Start(512);

        var echoTask = EchoServerAsync(echo, cts.Token);

        // 等端口就绪（转发端口由客户端进程监听，可能比本进程晚起来）
        await WaitForPortAsync(remotePort, TimeSpan.FromSeconds(60), cts.Token);

        var perConnection = (long)totalMegabytes * 1024 * 1024 / connections;
        Console.WriteLine(
            $"mode=load remote=127.0.0.1:{remotePort} echo=127.0.0.1:{echoPort} " +
            $"total={totalMegabytes}MiB chunk={chunkBytes / 1024}KiB conns={connections}");

        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var payload = new byte[chunkBytes];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = Pattern(i);
        }

        var workers = new Task[connections];

        for (var i = 0; i < connections; i++)
        {
            var index = i;
            workers[i] = Task.Run(async () =>
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, remotePort, cts.Token);

                var readBuffer = new byte[chunkBytes];
                var sent = 0L;

                while (sent < perConnection)
                {
                    var size = (int)Math.Min(chunkBytes, perConnection - sent);
                    await socket.SendAsync(payload.AsMemory(0, size), SocketFlags.None, cts.Token);

                    var filled = 0;
                    while (filled < size)
                    {
                        var read = await socket.ReceiveAsync(readBuffer.AsMemory(filled, size - filled), SocketFlags.None, cts.Token);
                        if (read == 0)
                        {
                            throw new EndOfStreamException($"连接 {index} 提前关闭");
                        }

                        filled += read;
                    }

                    // 逐字节校验回显内容：数据面任何「丢一段 / 踩内存」都会在这里暴露，
                    // 而不是被吞吐数字掩盖过去。
                    for (var offset = 0; offset < size; offset++)
                    {
                        var expected = Pattern((sent + offset) % payload.Length);
                        if (readBuffer[offset] != expected)
                        {
                            throw new InvalidDataException(
                                $"连接 {index} 数据损坏：偏移 {sent + offset} 期望 {expected} 实际 {readBuffer[offset]}");
                        }
                    }

                    sent += size;
                }
            }, cts.Token);
        }

        await Task.WhenAll(workers);

        sw.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        var bytes = (long)totalMegabytes * 1024 * 1024;
        var seconds = sw.Elapsed.TotalSeconds;

        Console.WriteLine("seconds   one-way(MiB/s)  link(MiB/s)  cpu-cores(this proc)  alloc(MiB)");
        Console.WriteLine(string.Format("{0,7:F2}  {1,15:F1}  {2,10:F1}  {3,20:F2}  {4,10:F1}",
            seconds, bytes / 1024d / 1024d / seconds, bytes * 2 / 1024d / 1024d / seconds,
            cpu.TotalSeconds / seconds, alloc / 1024d / 1024d));

        cts.Cancel();
        echo.Stop();
        await Task.WhenAny(echoTask, Task.Delay(1000));
    }

    /// <summary>确定性字节图案：回显校验用，避免「全 0 数据」把丢包 / 踩内存掩盖掉。</summary>
    private static byte Pattern(long offset) => (byte)(offset % 251);

    private static async Task EchoServerAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = await listener.AcceptSocketAsync(token);
                socket.NoDelay = true;
                _ = Task.Run(async () =>
                {
                    using (socket)
                    {
                        var buffer = new byte[64 * 1024];
                        try
                        {
                            while (true)
                            {
                                var read = await socket.ReceiveAsync(buffer, SocketFlags.None, token);
                                if (read == 0)
                                {
                                    break;
                                }

                                var offset = 0;
                                while (offset < read)
                                {
                                    offset += await socket.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, token);
                                }
                            }
                        }
                        catch
                        {
                            // 连接关闭
                        }
                    }
                }, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await probe.ConnectAsync(IPAddress.Loopback, port, token);
                return;
            }
            catch
            {
                await Task.Delay(250, token);
            }
        }

        throw new TimeoutException($"端口 {port} 在 {timeout.TotalSeconds:F0}s 内没有就绪");
    }

    // ---------------- 测试装置 ----------------

    /// <summary>
    /// 两端 MuxSession + 真实 loopback TCP 承载。客户端侧收到流就回显（把收到的字节原样写回去），
    /// 这样一条流上跑的是「上行 + 下行」两个方向同时满速。
    /// </summary>
    private sealed class MuxHarness : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Socket _clientSocket;
        private readonly SocketPipe _serverPipe;
        private readonly SocketPipe _clientPipe;
        private readonly MuxSession _server;
        private readonly MuxSession _client;
        private readonly Task _serverRun;
        private readonly Task _clientRun;
        private readonly CancellationTokenSource _cts = new();

        private MuxHarness(
            TcpListener listener, Socket clientSocket,
            SocketPipe serverPipe, SocketPipe clientPipe,
            MuxSession server, MuxSession client)
        {
            _listener = listener;
            _clientSocket = clientSocket;
            _serverPipe = serverPipe;
            _clientPipe = clientPipe;
            _server = server;
            _client = client;

            _serverRun = _server.RunAsync();
            _clientRun = _client.RunAsync();
        }

        public static async Task<MuxHarness> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(512);

            var connectTask = ConnectAsync(listener);
            var accepted = await ConnectAcceptAsync(listener);
            var clientSocket = await connectTask;

            var serverPipe = new SocketPipe(accepted);
            var clientPipe = new SocketPipe(clientSocket);

            var server = new MuxSession(serverPipe, isServer: true, NullLogger.Instance);
            MuxSession client = null!;
            client = new MuxSession(clientPipe, isServer: false, NullLogger.Instance,
                (stream, _) => EchoAsync(stream, client.Closed));

            return new MuxHarness(listener, clientSocket, serverPipe, clientPipe, server, client);
        }

        private static async Task<Socket> ConnectAsync(TcpListener listener)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(listener.LocalEndpoint);
            return socket;
        }

        private static async Task<Socket> ConnectAcceptAsync(TcpListener listener)
        {
            var socket = await listener.AcceptSocketAsync();
            socket.NoDelay = true;
            return socket;
        }

        /// <summary>开一条流，双向各搬运 <paramref name="bytes"/> 字节。</summary>
        public async Task TransferAsync(long bytes, int chunkBytes)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            await using var stream = await _server.OpenStreamAsync(
                new SwapRequest { Id = "bench", Host = "127.0.0.1", Port = 80, Proto = "tcp" }, cts.Token);

            var payload = new byte[chunkBytes];

            var writeTask = Task.Run(async () =>
            {
                var remaining = bytes;
                while (remaining > 0)
                {
                    var size = (int)Math.Min(payload.Length, remaining);
                    await stream.Output.WriteAsync(payload.AsMemory(0, size), cts.Token);
                    remaining -= size;
                }
            }, cts.Token);

            var readTask = Task.Run(async () => await DrainAsync(stream.Input, bytes, cts.Token), cts.Token);

            await Task.WhenAll(writeTask, readTask);
        }

        /// <summary>一批短连接：建流 → 发 N 字节 → 读回 N 字节 → 关流。</summary>
        public async Task<int> RoundTripBatchAsync(int total, int concurrency, int payloadBytes)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            var done = 0;
            var workers = new Task[concurrency];
            var perWorker = (int)Math.Ceiling((double)total / concurrency);
            var payload = new byte[payloadBytes];
            var readBuffer = new byte[payloadBytes];

            for (var i = 0; i < concurrency; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    for (var n = 0; n < perWorker; n++)
                    {
                        await using var stream = await _server.OpenStreamAsync(
                            new SwapRequest { Id = "conn", Host = "127.0.0.1", Port = 80, Proto = "tcp" }, cts.Token);

                        await stream.Output.WriteAsync(payload, cts.Token);

                        var filled = 0;
                        while (filled < payloadBytes)
                        {
                            var result = await stream.Input.ReadAsync(cts.Token);
                            var buffer = result.Buffer;
                            var take = (int)Math.Min(buffer.Length, payloadBytes - filled);
                            buffer.Slice(0, take).CopyTo(readBuffer.AsSpan(filled));
                            stream.Input.AdvanceTo(buffer.GetPosition(take));
                            filled += take;
                        }

                        Interlocked.Increment(ref done);
                    }
                }, cts.Token);
            }

            await Task.WhenAll(workers);
            return done;
        }

        private static async Task EchoAsync(MuxStream stream, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var result = await stream.Input.ReadAsync(token);
                    var buffer = result.Buffer;

                    if (buffer.IsEmpty)
                    {
                        stream.Input.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }

                    foreach (var segment in buffer)
                    {
                        await stream.Output.WriteAsync(segment, token);
                    }

                    stream.Input.AdvanceTo(buffer.End);
                }
            }
            catch (Exception)
            {
                // 会话关闭
            }
            finally
            {
                try
                {
                    await stream.Output.CompleteAsync();
                }
                catch
                {
                }
            }
        }

        private static async Task DrainAsync(PipeReader reader, long bytes, CancellationToken token)
        {
            var remaining = bytes;
            while (remaining > 0)
            {
                var result = await reader.ReadAsync(token);
                var buffer = result.Buffer;
                var take = (int)Math.Min(buffer.Length, remaining);
                reader.AdvanceTo(buffer.GetPosition(take));
                remaining -= take;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    throw new EndOfStreamException();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _server.DisposeAsync();
            await _client.DisposeAsync();
            await Task.WhenAny(Task.WhenAll(_serverRun, _clientRun), Task.Delay(2000));
            _serverPipe.Dispose();
            _clientPipe.Dispose();
            _clientSocket.Dispose();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    /// <summary>真实 socket 上的 <see cref="IDuplexPipe"/>，与生产用的 SocketDuplexPipe 同构。</summary>
    private sealed class SocketPipe : IDuplexPipe, IDisposable
    {
        private readonly NetworkStream _stream;

        public SocketPipe(Socket socket)
        {
            _stream = new NetworkStream(socket, ownsSocket: true);

            Input = PipeReader.Create(_stream, new StreamPipeReaderOptions(
                bufferSize: 64 * 1024, minimumReadSize: 32 * 1024));

            Output = PipeWriter.Create(_stream, new StreamPipeWriterOptions(
                minimumBufferSize: 64 * 1024));
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public void Dispose()
        {
            Input.Complete();
            Output.Complete();
            _stream.Dispose();
        }
    }
}
