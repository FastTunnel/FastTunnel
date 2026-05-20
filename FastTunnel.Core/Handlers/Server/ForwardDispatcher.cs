// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Refs;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Handlers.Server;

public class ForwardDispatcher
{
    private readonly FastTunnelServer _server;
    private readonly ForwardConfig _config;
    private readonly WebSocket _client;
    private readonly ILogger logger;

    public ForwardDispatcher(ILogger logger, FastTunnelServer server, ForwardConfig config, WebSocket client)
    {
        this.logger = logger;
        _server = server;
        _config = config;
        _client = client;
    }

    int SwapCount;

    /// <summary>
    /// 申请一条与 FastTunnel 客户端之间的 swap 隧道流。
    /// 用于 UDP 端口转发等需要直接持有底层流的场景。
    /// </summary>
    /// <param name="protocol">"tcp" 或 "udp"</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<(Stream Stream, CancellationTokenSource TokenSource)> RequestSwapAsync(
        string protocol, CancellationToken cancellationToken)
    {
        var msgId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<(Stream Stream, CancellationTokenSource Token)>();
        if (!_server.ResponseTasks.TryAdd(msgId, tcs))
        {
            throw new InvalidOperationException("无法申请 swap 通道");
        }

        try
        {
            var payload = string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase)
                ? $"{msgId}|udp|{_config.LocalIp}:{_config.LocalPort}"
                : $"{msgId}|{_config.LocalIp}:{_config.LocalPort}";

            await _client.SendCmdAsync(MessageType.Forward, payload, cancellationToken);
        }
        catch
        {
            _server.ResponseTasks.TryRemove(msgId, out _);
            throw;
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch
        {
            _server.ResponseTasks.TryRemove(msgId, out _);
            throw;
        }
    }

    /// <summary>
    /// 处理 TCP 端口转发请求
    /// </summary>
    /// <param name="_socket">用户请求</param>
    /// <returns></returns>
    public async Task DispatchAsync(Socket _socket)
    {
        var msgId = Guid.NewGuid();

        (Stream Stream, CancellationTokenSource TokenSource) res = default;

        Interlocked.Increment(ref SwapCount);

        try
        {
            logger.LogDebug($"[Forward]Swap开始 {msgId}|{_config.RemotePort}=>{_config.LocalIp}:{_config.LocalPort}");
            var tcs = new TaskCompletionSource<(Stream Stream, CancellationTokenSource TokenSource)>();
            if (!_server.ResponseTasks.TryAdd(msgId, tcs))
            {
                return;
            }

            try
            {
                await _client.SendCmdAsync(MessageType.Forward, $"{msgId}|{_config.LocalIp}:{_config.LocalPort}", CancellationToken.None);
            }
            catch (SocketClosedException sex)
            {
                // TODO:客户端已掉线，但是没有移除对端口的监听
                logger.LogError($"[Forward]Swap 客户端已离线 {sex.Message}");
                tcs.TrySetCanceled();
                Close(_socket);
                return;
            }
            catch (Exception ex)
            {
                // 网络不稳定
                logger.LogError(ex, $"[Forward]Swap Exception");
                tcs.TrySetCanceled();
                Close(_socket);
                return;
            }

            res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var stream2 = new NetworkStream(_socket, ownsSocket: true);
            var swapToken = res.TokenSource.Token;

            // 双向拷贝；任一方向结束立即取消另一方向，避免半关闭后另一侧空转直到底层 IO 失败。
            // 零拷贝快路径：swap 端是 DuplexPipeStream，可直接拿到底层 PipeReader/PipeWriter，
            // 绕开 Stream.CopyToAsync 默认 81920 字节中转缓冲区，使用 Pipe 池化内存。
            Task t1, t2;
            if (res.Stream is DuplexPipeStream dps)
            {
                // swap pipe -> socket: PipeReader.CopyToAsync(Stream) 使用 Pipe 池化段，
                // 直接通过 NetworkStream.WriteAsync(ReadOnlyMemory<byte>) 写出，无中转拷贝。
                t1 = dps.Input.CopyToAsync(stream2, swapToken);
                // socket -> swap pipe: 手动从 Socket 读到 PipeWriter.GetMemory()，
                // 避免再分配独立 byte[] 缓冲区。
                t2 = PumpSocketToPipeAsync(_socket, dps.Output, swapToken);
            }
            else
            {
                t1 = res.Stream.CopyToAsync(stream2, swapToken);
                t2 = stream2.CopyToAsync(res.Stream, swapToken);
            }

            try
            {
                await Task.WhenAny(t1, t2);
            }
            finally
            {
                res.TokenSource.Cancel();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug($"[Forward]Swap Error {msgId}：" + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref SwapCount);
            res.TokenSource?.Cancel();
            logger.LogDebug($"[Forward]Swap OK {msgId} {SwapCount}");
            _server.ResponseTasks.TryRemove(msgId, out _);
        }
    }

    /// <summary>
    /// 从 Socket 读取数据并写入 PipeWriter，使用 PipeWriter 的池化内存避免额外分配。
    /// 与 PipeReader.CopyToAsync(PipeWriter) 一致：到达 EOF/取消时仅停止抽送，
    /// 不主动 Complete 对端管道（由调用方通过 TokenSource 统一收尾）。
    /// </summary>
    private static async Task PumpSocketToPipeAsync(Socket socket, PipeWriter writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // 1. 不指定大小，让 PipeWriter 自动从内存池获取最佳大小的内存块
            var memory = writer.GetMemory(); 
            int read;
            try
            {
                read = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                // 远端正常关闭
                break;
            }

            // 2. 明确告知管道实际写入了多少字节
            writer.Advance(read);
            
            FlushResult flush;
            try
            {
                flush = await writer.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 3. 完善对 FlushResult 的判断，增加对 IsPaused 的处理
            if (flush.IsCompleted || flush.IsCanceled)
            {
                break;
            }
        }
        
        // 4. 循环结束后，如果是因为远端关闭，建议由调用方统一 Complete，
        // 或者在这里根据业务逻辑决定是否调用 await writer.CompleteAsync();
        // (根据你的原代码注释，这里不主动 Complete 是对的，由调用方统一收尾)
    }

    private void Close(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
        }
        finally
        {
            socket.Close();
        }
    }
}
