// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FastTunnel.LoadRunner;

/// <summary>
/// HTTP/1.1 定速压测工具（开环）。
///
/// 为什么要自己写：需要「按目标 RPS 精确发压」+「直接指定 Host 头（虚拟主机路由）」+
/// 「逐请求记录延迟」，并且要把「没压到目标速率」和「压到了但延迟/错误率炸了」区分开。
///
/// 关键设计：
/// - 一条连接一个线程、阻塞 I/O：没有每请求的 CancellationTokenSource / Task 分配，
///   超时交给内核（ReceiveTimeout），压测工具自身的开销不会混进被测延迟。
/// - 全局槽位定速：每个请求领取一个「应发送时刻」，聚合速率精确等于目标 RPS；
///   机器扛不住时表现为「实际速率低于目标 + 落后于计划」，而不是把队列无限堆高。
/// - 延迟从真正写出请求那一刻算起，不含排队时间——排队单独用 "LateSendRatio" 度量。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "serve")
        {
            return OriginServer.Run(args);
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        // Windows 默认定时器精度约 15.6ms，定速用的 Thread.Sleep 会集体晚 5ms 以上，
        // 把「落后计划」的指标打满。Win10 2004 起该设置只影响本进程，不会干扰被测端。
        NativeMethods.TimeBeginPeriod(1);

        try
        {
            var result = Run(options);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

            if (options.OutPath != null)
            {
                File.WriteAllText(options.OutPath, json);
            }

            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
        }
    }

    private static LoadResult Run(Options options)
    {
        var request = Encoding.ASCII.GetBytes(
            $"GET {options.Path} HTTP/1.1\r\n" +
            $"Host: {options.Host}\r\n" +
            "Accept: */*\r\n" +
            "User-Agent: FastTunnel-LoadRunner/1.0\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

        var pacer = new SlotPacer(options.Rps);
        using var stop = new CancellationTokenSource();
        var token = stop.Token;
        var runStart = Stopwatch.GetTimestamp();

        var workers = new Worker[options.Connections];
        var threads = new Thread[options.Connections];

        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = new Worker(i, options, request, pacer, token, runStart);
            threads[i] = new Thread(workers[i].Run)
            {
                IsBackground = true,
                Name = $"load-{i}",
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        var measureEnd = options.Warmup + options.Duration;
        var wall = Stopwatch.StartNew();
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;

        // 测量窗口走完就收工（留一点收尾时间让最后一个请求把结果记完）
        while (wall.Elapsed.TotalSeconds < measureEnd && !token.IsCancellationRequested)
        {
            Thread.Sleep(10);
        }

        var measuredSeconds = Math.Max(0.001, wall.Elapsed.TotalSeconds - options.Warmup);
        var cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds;
        stop.Cancel();

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(15));
        }

        var result = Summarize(options, workers, measuredSeconds);
        result.SelfCpuSeconds = Math.Round(cpuSeconds, 2);
        result.SelfCores = Math.Round(cpuSeconds / measuredSeconds, 2);
        return result;
    }

    private static LoadResult Summarize(Options options, Worker[] workers, double measureSeconds)
    {
        var samples = new List<Sample>(1 << 16);
        foreach (var worker in workers)
        {
            worker.CollectSamples(samples);
        }

        var latencies = new List<double>(samples.Count);
        var errors = 0;
        long bytes = 0;
        var lateSends = 0;
        var windowStart = options.Warmup;
        var windowEnd = options.Warmup + options.Duration;

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.AtSeconds < windowStart || sample.AtSeconds >= windowEnd)
            {
                continue; // 预热期 / 收尾期的样本不计入统计
            }
            latencies.Add(sample.LatencyMs);
            bytes += sample.Bytes;

            if (!sample.Ok)
            {
                errors++;
            }

            if (sample.LateByMs > 5)
            {
                lateSends++;
            }
        }

        var sorted = latencies.ToArray();
        Array.Sort(sorted);

        var total = sorted.Length;
        return new LoadResult
        {
            Label = options.Label,
            TargetRps = options.Rps,
            Connections = options.Connections,
            WarmupSeconds = options.Warmup,
            DurationSeconds = Math.Round(measureSeconds, 2),
            Requests = total,
            Errors = errors,
            ErrorRate = total == 0 ? 1 : Math.Round((double)errors / total, 6),
            AchievedRps = Math.Round(total / measureSeconds, 1),
            ThroughputMiBps = Math.Round(bytes / 1024d / 1024d / measureSeconds, 3),
            LateSendRatio = total == 0 ? 0 : Math.Round((double)lateSends / total, 4),
            LatencyMs = new LatencyStats
            {
                Mean = total == 0 ? 0 : Math.Round(sorted.Average(), 3),
                P50 = Percentile(sorted, 0.50),
                P90 = Percentile(sorted, 0.90),
                P99 = Percentile(sorted, 0.99),
                P999 = Percentile(sorted, 0.999),
                Max = total == 0 ? 0 : Math.Round(sorted[^1], 3),
            },
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return Math.Round(sorted[index], 3);
    }

    // ---------------- 定速 ----------------

    /// <summary>
    /// 全局槽位定速：第 n 个请求应在 startTicks + n / rps 时刻发出。
    /// 各线程原子领取槽位，聚合发送速率就等于目标 RPS；
    /// 一旦机器扛不住、槽位时间全落在过去，定速自动退化为「能发多快发多快」。
    /// </summary>
    private sealed class SlotPacer
    {
        private readonly long _intervalTicks;
        private readonly long _startTicks;
        private long _slot;

        public SlotPacer(int rps)
        {
            _intervalTicks = Math.Max(1, Stopwatch.Frequency / Math.Max(1, rps));
            _startTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5; // 给建连留 200ms
        }

        public long NextDueTicks() => _startTicks + Interlocked.Increment(ref _slot) * _intervalTicks;

        public static void WaitUntil(long dueTicks, CancellationToken token)
        {
            while (true)
            {
                var remaining = dueTicks - Stopwatch.GetTimestamp();
                if (remaining <= 0 || token.IsCancellationRequested)
                {
                    return;
                }

                var ms = remaining * 1000.0 / Stopwatch.Frequency;
                if (ms >= 1)
                {
                    // 1ms 定时器精度：直接睡，不补自旋/让出。
                    // 自旋或 Thread.Yield 在 1000 RPS 下会白烧 1~2 个核，发压机会和被测端抢 CPU。
                    Thread.Sleep((int)ms);
                }
                else
                {
                    Thread.Sleep(0);
                }
            }
        }
    }

    private readonly struct Sample
    {
        public Sample(bool ok, double latencyMs, int bytes, double lateByMs, double atSeconds)
        {
            Ok = ok;
            LatencyMs = latencyMs;
            Bytes = bytes;
            LateByMs = lateByMs;
            AtSeconds = atSeconds;
        }

        public bool Ok { get; }

        public double LatencyMs { get; }

        public int Bytes { get; }

        public double LateByMs { get; }

        /// <summary>该请求发出时刻（相对压测启动的秒数），用于把预热期样本排除在统计外。</summary>
        public double AtSeconds { get; }
    }

    // ---------------- 连接线程 ----------------

    private sealed class Worker
    {
        private readonly int _id;
        private readonly Options _options;
        private readonly byte[] _request;
        private readonly SlotPacer _pacer;
        private readonly CancellationToken _token;
        private readonly long _runStartTicks;
        private readonly List<Sample> _samples = new(8192);
        private readonly byte[] _readBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        private Socket? _socket;
        private bool _alive;

        public Worker(
            int id, Options options, byte[] request, SlotPacer pacer, CancellationToken token, long runStartTicks)
        {
            _id = id;
            _options = options;
            _request = request;
            _pacer = pacer;
            _token = token;
            _runStartTicks = runStartTicks;
        }

        public void CollectSamples(List<Sample> target)
        {
            lock (_samples)
            {
                target.AddRange(_samples);
            }
        }

        public void Run()
        {
            // 让连接错开建立：避免所有线程同一瞬间 SYN 冲垮被测端，也避免把建连成本算进测量窗口
            Thread.Sleep(Math.Min(300, _id * 600 / Math.Max(1, _options.Connections)));

            try
            {
                while (!_token.IsCancellationRequested)
                {
                    if (!EnsureConnected())
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    var due = _pacer.NextDueTicks();
                    SlotPacer.WaitUntil(due, _token);

                    if (_token.IsCancellationRequested)
                    {
                        break;
                    }

                    var lateBy = (Stopwatch.GetTimestamp() - due) * 1000.0 / Stopwatch.Frequency;
                    var start = Stopwatch.GetTimestamp();

                    try
                    {
                        SendAll(_request);
                        var response = ReadResponse();

                        var latency = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                        Record(new Sample(response.Ok, latency, response.BodyBytes, lateBy, AtSeconds(start)));

                        if (response.Close)
                        {
                            DropSocket();
                        }
                    }
                    catch (Exception)
                    {
                        var latency = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                        Record(new Sample(false, latency, 0, lateBy, AtSeconds(start)));
                        DropSocket();
                    }
                }
            }
            finally
            {
                DropSocket();
                ArrayPool<byte>.Shared.Return(_readBuffer);
            }
        }

        private void Record(Sample sample)
        {
            lock (_samples)
            {
                _samples.Add(sample);
            }
        }

        private double AtSeconds(long ticks) => (ticks - _runStartTicks) * 1.0 / Stopwatch.Frequency;

        private bool EnsureConnected()
        {
            if (_alive && _socket != null)
            {
                return true;
            }

            DropSocket();

            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    ReceiveTimeout = _options.TimeoutMs,
                    SendTimeout = _options.TimeoutMs,
                };

                socket.Connect(new IPEndPoint(_options.ConnectAddress, _options.ConnectPort));
                _socket = socket;
                _alive = true;
                return true;
            }
            catch (Exception)
            {
                _alive = false;
                return false;
            }
        }

        private void SendAll(byte[] payload)
        {
            var sent = 0;
            while (sent < payload.Length)
            {
                sent += _socket!.Send(payload, sent, payload.Length - sent, SocketFlags.None);
            }
        }

        private Response ReadResponse()
        {
            var reader = new SocketReader(_socket!, _readBuffer);
            var header = reader.ReadHeaderBlock();

            var status = ParseStatus(header);
            var close = header.Contains("Connection: close", StringComparison.OrdinalIgnoreCase);

            int bodyBytes;
            if (header.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
            {
                bodyBytes = reader.ReadChunkedBody();
            }
            else
            {
                var contentLength = ParseContentLength(header);
                if (contentLength < 0)
                {
                    throw new IOException("响应既没有 Content-Length 也不是 chunked");
                }

                reader.ReadExactly(contentLength);
                bodyBytes = contentLength;
            }

            return new Response(status == 200, bodyBytes, close);
        }

        private void DropSocket()
        {
            _alive = false;

            try
            {
                _socket?.Dispose();
            }
            catch
            {
                // ignore
            }

            _socket = null;
        }

        private static int ParseStatus(string header)
        {
            // HTTP/1.1 200 OK
            var lineEnd = header.IndexOf('\r');
            var line = lineEnd > 0 ? header[..lineEnd] : header;
            var parts = line.Split(' ');
            return parts.Length >= 2 && int.TryParse(parts[1], out var status) ? status : 0;
        }

        private static int ParseContentLength(string header)
        {
            foreach (var line in header.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var value) ? value : -1;
                }
            }

            return -1;
        }
    }

    private readonly struct Response
    {
        public Response(bool ok, int bodyBytes, bool close)
        {
            Ok = ok;
            BodyBytes = bodyBytes;
            Close = close;
        }

        public bool Ok { get; }

        public int BodyBytes { get; }

        public bool Close { get; }
    }

    /// <summary>同一份缓冲里的 HTTP 响应读取器：头部逐行、正文按长度/分块。</summary>
    private sealed class SocketReader
    {
        private readonly Socket _socket;
        private readonly byte[] _buffer;
        private int _start;
        private int _end;

        public SocketReader(Socket socket, byte[] buffer)
        {
            _socket = socket;
            _buffer = buffer;
        }

        public string ReadHeaderBlock()
        {
            var builder = new StringBuilder(512);

            while (true)
            {
                if (_start >= _end && !Fill())
                {
                    throw new IOException("连接被对端关闭");
                }

                var index = IndexOfDoubleCrlf(_buffer, _start, _end);
                if (index >= 0)
                {
                    builder.Append(Encoding.ASCII.GetString(_buffer, _start, index - _start + 4));
                    _start = index + 4;
                    return builder.ToString();
                }

                builder.Append(Encoding.ASCII.GetString(_buffer, _start, _end - _start));
                _start = _end = 0;

                if (builder.Length > 64 * 1024)
                {
                    throw new IOException("响应头过大");
                }
            }
        }

        public void ReadExactly(int count)
        {
            var remaining = count;

            while (remaining > 0)
            {
                if (_start >= _end && !Fill())
                {
                    throw new IOException("响应体被截断");
                }

                var take = Math.Min(remaining, _end - _start);
                _start += take;
                remaining -= take;
            }
        }

        public int ReadChunkedBody()
        {
            var total = 0;

            while (true)
            {
                var line = ReadLine();
                var sizeText = line.Split(';')[0].Trim();
                var size = int.Parse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                if (size == 0)
                {
                    // 末尾的 trailer / 空行
                    while (ReadLine().Length > 0)
                    {
                    }

                    return total;
                }

                ReadExactly(size);
                total += size;

                // 块尾 CRLF
                ReadLine();
            }
        }

        private string ReadLine()
        {
            var builder = new StringBuilder(16);

            while (true)
            {
                if (_start >= _end && !Fill())
                {
                    throw new IOException("chunked 响应被截断");
                }

                while (_start < _end)
                {
                    var value = _buffer[_start++];
                    if (value == (byte)'\n')
                    {
                        return builder.ToString().TrimEnd('\r');
                    }

                    builder.Append((char)value);
                }
            }
        }

        private bool Fill()
        {
            _start = 0;
            _end = _socket.Receive(_buffer, 0, _buffer.Length, SocketFlags.None);
            return _end > 0;
        }

        private static int IndexOfDoubleCrlf(byte[] buffer, int start, int end)
        {
            for (var i = start; i + 3 < end; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                    && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                {
                    return i;
                }
            }

            return -1;
        }
    }

    // ---------------- 参数与结果 ----------------

    private sealed class Options
    {
        public string Label = "load";
        public string Host = "127.0.0.1";
        public IPAddress ConnectAddress = IPAddress.Loopback;
        public int ConnectPort = 80;
        public bool ConnectExplicit;
        public string Path = "/";
        public int Rps = 1000;
        public int Connections = 64;
        public double Warmup = 2;
        public double Duration = 5;
        public int TimeoutMs = 10_000;
        public string? OutPath;

        public const string Usage =
            "用法: FastTunnel.LoadRunner --host bench.ft.test --port 17270 --path /bench/index.html " +
            "--rps 5000 --connections 256 --warmup 3 --duration 9 [--connect 127.0.0.1:17270] " +
            "[--out result.json] [--label 名称]\n" +
            "不给 --connect 时按 --host 做 DNS 解析（hosts 生效时就是真实 DNS 路径）。";

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var name = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} 缺少取值");

                switch (name)
                {
                    case "--connect":
                        var connect = Next();
                        var separator = connect.LastIndexOf(':');
                        options.ConnectAddress = IPAddress.Parse(connect[..separator]);
                        options.ConnectPort = int.Parse(connect[(separator + 1)..], CultureInfo.InvariantCulture);
                        options.ConnectExplicit = true;
                        break;
                    case "--port":
                        options.ConnectPort = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--host":
                        options.Host = Next();
                        break;
                    case "--path":
                        options.Path = Next();
                        break;
                    case "--rps":
                        options.Rps = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--connections":
                        options.Connections = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--warmup":
                        options.Warmup = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--duration":
                        options.Duration = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--timeout":
                        options.TimeoutMs = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--out":
                        options.OutPath = Next();
                        break;
                    case "--label":
                        options.Label = Next();
                        break;
                    default:
                        throw new ArgumentException($"未知参数 {name}");
                }
            }

            if (options.Rps <= 0 || options.Connections <= 0 || options.Duration <= 0)
            {
                throw new ArgumentException("rps / connections / duration 必须大于 0");
            }

            if (!options.ConnectExplicit)
            {
                // 按主机名解析：hosts 里配了 bench.ft.test -> 127.0.0.1 时，走的就是真实 DNS 路径
                var addresses = Dns.GetHostAddresses(options.Host);
                var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                           ?? throw new IOException($"域名 {options.Host} 没有解析到 IPv4 地址");
                options.ConnectAddress = ipv4;
            }

            return options;
        }
    }

    private sealed class LoadResult
    {        public string Label { get; set; } = string.Empty;

        public int TargetRps { get; set; }

        public int Connections { get; set; }

        public double WarmupSeconds { get; set; }

        public double DurationSeconds { get; set; }

        public int Requests { get; set; }

        public int Errors { get; set; }

        public double ErrorRate { get; set; }

        public double AchievedRps { get; set; }

        public double ThroughputMiBps { get; set; }

        /// <summary>发送时刻晚于计划 5ms 以上的请求占比：机器扛不住时的直接信号。</summary>
        public double LateSendRatio { get; set; }

        /// <summary>压测工具自身在测量窗口内的 CPU 秒数：用来判断"是隧道慢"还是"发压机先扛不住"。</summary>
        public double SelfCpuSeconds { get; set; }

        /// <summary>压测工具自身的平均 CPU 核数。</summary>
        public double SelfCores { get; set; }

        public LatencyStats LatencyMs { get; set; } = new();
    }

    private sealed class LatencyStats
    {
        public double Mean { get; set; }

        public double P50 { get; set; }

        public double P90 { get; set; }

        public double P99 { get; set; }

        public double P999 { get; set; }

        public double Max { get; set; }
    }
}
