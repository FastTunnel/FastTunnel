// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using FastTunnel.Core.Handlers.Server;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Listener;

public class PortProxyListener
{
    private readonly ILogger _logerr;

    public string ListenIp { get; set; }

    public int ListenPort { get; set; }

    private int m_numConnectedSockets;
    private bool shutdown;
    private ForwardDispatcher _requestDispatcher;
    private readonly Socket listenSocket;
    private readonly WebSocket client;

    public PortProxyListener(string ip, int port, ILogger logerr, WebSocket client)
    {
        this.client = client;
        _logerr = logerr;
        this.ListenIp = ip;
        this.ListenPort = port;

        var ipa = IPAddress.Parse(ListenIp);
        var localEndPoint = new IPEndPoint(ipa, ListenPort);

        listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listenSocket.Bind(localEndPoint);
    }

    public void Start(ForwardDispatcher requestDispatcher)
    {
        shutdown = false;
        _requestDispatcher = requestDispatcher;

        listenSocket.Listen();

        StartAccept(null);
    }

    private void StartAccept(SocketAsyncEventArgs acceptEventArg)
    {
        _logerr.LogDebug($"【{ListenIp}:{ListenPort}】: StartAccept");
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

            _logerr.LogInformation($"【{ListenIp}:{ListenPort}】Accepted. There are {{0}} clients connected to the port",
                m_numConnectedSockets);

            // 将此客户端交由Dispatcher进行管理
            _requestDispatcher.DispatchAsync(accept, client);

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
