// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Listener;

/// <summary>
/// TCP 端口转发监听器
/// </summary>
public class PortProxyListener : IPortListener
{
    private readonly ILogger _logerr;

    public string ListenIp { get; }

    public int ListenPort { get; }

    private int m_numConnectedSockets;
    private bool shutdown;
    private readonly ForwardDispatcher _requestDispatcher;
    private readonly Socket listenSocket;

    public PortProxyListener(string ip, int port, ILogger logerr, ForwardDispatcher requestDispatcher)
    {
        _logerr = logerr;
        _requestDispatcher = requestDispatcher;
        this.ListenPort = port;

        // 通配地址走双栈：一个 socket 同时接 IPv6 与 IPv4 公网连接，
        // IPv4 对端在内核里表现为 ::ffff:x.x.x.x，数据面无需区分。
        listenSocket = NetUtil.BindTcp(NetUtil.ParseListenAddress(ip), port);

        // 日志里展示实际生效的family，避免「配置 0.0.0.0、实际在收 IPv6」的困惑
        this.ListenIp = listenSocket.AddressFamily == AddressFamily.InterNetworkV6
            ? (listenSocket.DualMode ? "::" : "[::]")
            : "0.0.0.0";
    }

    public void Start()
    {
        shutdown = false;
        listenSocket.Listen();

        StartAccept(null);
    }

    private void StartAccept(SocketAsyncEventArgs acceptEventArg)
    {
        if (acceptEventArg == null)
        {
            acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(AcceptEventArg_Completed);
        }
        else
        {
            // socket must be cleared since the context object is being reused
            acceptEventArg.AcceptSocket = null;
        }

        var willRaiseEvent = listenSocket.AcceptAsync(acceptEventArg);
        if (!willRaiseEvent)
        {
            ProcessAcceptAsync(acceptEventArg);
        }
    }

    private void ProcessAcceptAsync(SocketAsyncEventArgs e)
    {
        if (e.SocketError == SocketError.Success)
        {
            var accept = e.AcceptSocket;

            Interlocked.Increment(ref m_numConnectedSockets);

            _logerr.LogDebug($"【{ListenIp}:{ListenPort}】Accepted. There are {{0}} clients connected to the port",
                m_numConnectedSockets);

            // 将此客户端交由Dispatcher进行管理
            _requestDispatcher.DispatchAsync(accept);

            // Accept the next connection request
            StartAccept(e);
        }
        else
        {
            Stop();
        }
    }

    private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
    {
        ProcessAcceptAsync(e);
    }

    public void Stop()
    {
        if (shutdown)
            return;

        try
        {
            if (listenSocket.Connected)
            {
                listenSocket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            shutdown = true;
            listenSocket.Close();
        }
    }
}
