using Xunit;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Tests.Support;

/// <summary>测试用的最小 SOCKS5 客户端：拼报文、精确收取、以及一次完整的「握手 + 回显」往返。</summary>
public static class SocksProbe
{
    public static byte[] Greeting() => [5, 1, 0];

    /// <summary>CONNECT 请求；atyp 为 3 时主机名走域名编码，否则按 address 的字节编码。</summary>
    public static byte[] Request(string host, int port, byte command = 1, byte atyp = 3, string? address = null)
    {
        var head = new List<byte> { 5, command, 0, atyp };
        if (atyp == 3)
        {
            var name = Encoding.ASCII.GetBytes(host);
            head.Add((byte)name.Length);
            head.AddRange(name);
        }
        else
        {
            head.AddRange(IPAddress.Parse(address!).GetAddressBytes());
        }

        head.Add((byte)(port >> 8));
        head.Add((byte)(port & 0xFF));
        return [.. head];
    }

    public static async Task<byte[]> ReadExactAsync(Socket socket, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(read), token);
            if (count == 0)
                throw new IOException("SOCKS 对端提前关闭");
            read += count;
        }

        return buffer;
    }

    /// <summary>连上 SOCKS 出口，握手成功后发一段数据并等回显；隧道被切断时回空数组。</summary>
    public static async Task<byte[]> RoundTripAsync(int socksPort, string host, int targetPort,
        byte[] payload, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, socksPort), token);
        await socket.SendAsync(Greeting().Concat(Request(host, targetPort)).ToArray(), SocketFlags.None, token);
        Assert.Equal(new byte[] { 5, 0 }, await ReadExactAsync(socket, 2, token));
        Assert.Equal(0, (await ReadExactAsync(socket, 10, token))[1]);

        await socket.SendAsync(payload, SocketFlags.None, token);
        var reply = new byte[payload.Length];
        var read = await socket.ReceiveAsync(reply, token);
        return read == 0 ? [] : reply[..read];
    }

    /// <summary>本机回环上的一对已连接 Socket，用于直接驱动 SOCKS5 协议。</summary>
    public static async Task<(Socket Client, Socket Server)> PairAsync(CancellationToken token)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            var connected = client.ConnectAsync(endpoint.Address, endpoint.Port, token).AsTask();
            var accepted = listener.AcceptAsync(token).AsTask();
            await Task.WhenAll(connected, accepted);
            return (client, accepted.Result);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally
        {
            listener.Dispose();
        }
    }

    /// <summary>占一个空闲回环端口，交给被测监听使用。</summary>
    public static int FreePort()
    {
        var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Dispose();
        return port;
    }
}
