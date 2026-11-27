using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

/// <summary>
/// Windows 的 wintun 虚拟网卡（wintun.dll 0.14 及以上）。读取放在独立后台线程里轮询等待事件，
/// 地址和 MTU 用 <c>netsh</c> 下发；缺少 DLL、没有管理员权限或命令失败时只留日志并返回 null。
/// </summary>
internal sealed class WindowsTun : ITunDevice
{
    private const string Library = "wintun.dll";
    private const uint RingCapacity = 0x400_000;
    private const uint WaitMilliseconds = 200;
    private const uint WaitEvent0 = 0x0000_0000;
    private const uint WaitTimeout = 0x0000_0102;

    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly Thread reader;
    private nint adapter;
    private nint session;
    private nint readEvent;
    private volatile bool stopped;

    private WindowsTun(string name)
    {
        Name = name;
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "FastTunnel wintun" };
    }

    public string Name { get; }

    internal static WindowsTun? TryCreate(TunSetup setup, ILogger logger)
    {
        WindowsTun? tun = null;
        try
        {
            tun = Create(setup, logger);
        }
        catch (DllNotFoundException)
        {
            logger.LogWarning("找不到 wintun.dll，L3 组网未启用；请从 wintun 发行包把 wintun.dll 放到客户端同目录");
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.LogWarning("wintun.dll 版本过低（缺少 {Entry}），L3 组网未启用；请更新到 0.14 或更高版本", ex.Message);
            return null;
        }

        if (tun == null)
        {
            return null;
        }

        if (!tun.Configure(setup, logger))
        {
            tun.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }

        tun.reader.Start();
        logger.LogInformation("虚拟网卡 {Name} 已就绪：{Address}/{Mask}，MTU {Mtu}", setup.Name, setup.Address, setup.NetMask, setup.Mtu);
        return tun;
    }

    private static WindowsTun? Create(TunSetup setup, ILogger logger)
    {
        var wintun = Native.CreateSession("FastTunnel", setup.Name, null);
        if (wintun == 0)
        {
            logger.LogWarning("WintunCreateSession 失败（Win32 错误 {Code}），L3 组网未启用", Marshal.GetLastPInvokeError());
            return null;
        }

        try
        {
            // 上次留下的适配器直接复用，否则新建；两条路都需要管理员权限
            var adapter = Native.OpenAdapter(setup.Name);
            if (adapter == 0)
            {
                adapter = Native.CreateAdapter(wintun, setup.Name, setup.Name, 0);
            }

            if (adapter == 0)
            {
                logger.LogWarning("创建虚拟网卡 {Name} 失败（Win32 错误 {Code}）。L3 组网需要管理员权限，请以管理员身份运行客户端",
                    setup.Name, Marshal.GetLastPInvokeError());
                return null;
            }

            var tun = new WindowsTun(setup.Name) { adapter = adapter };
            tun.session = Native.StartSession(adapter, RingCapacity);
            if (tun.session == 0)
            {
                logger.LogWarning("启动 wintun 会话失败（Win32 错误 {Code}）", Marshal.GetLastPInvokeError());
                tun.Dispose();
                return null;
            }

            tun.readEvent = Native.GetReadWaitEvent(tun.session);
            if (tun.readEvent == 0)
            {
                logger.LogWarning("获取 wintun 读事件失败（Win32 错误 {Code}）", Marshal.GetLastPInvokeError());
                tun.Dispose();
                return null;
            }

            return tun;
        }
        finally
        {
            Native.CloseWintun(wintun);
        }
    }

    /// <summary>写地址、MTU 并接通接口；netsh 失败通常是没有以管理员运行。</summary>
    private bool Configure(TunSetup setup, ILogger logger)
    {
        foreach (var args in new[]
        {
            new[] { "interface", "ip", "set", "address", $"interface={setup.Name}", "static", setup.Address, "mask", setup.NetMask, "store=active" },
            new[] { "interface", "ip", "set", "subinterface", setup.Name, $"mtu={setup.Mtu}", "store=active" },
            new[] { "interface", "set", "interface", setup.Name, "connect" },
        })
        {
            var (code, error) = TunProcess.Run("netsh", args);
            if (code != 0)
            {
                logger.LogWarning("执行 netsh {Command} 失败（退出码 {Code}）：{Error}。配置虚拟网卡需要管理员权限",
                    string.Join(' ', args), code, error.Length == 0 ? "无输出" : error);
                return false;
            }
        }

        return true;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ReadCoreAsync(buffer, token);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var buffer = Native.AllocateSendPacket(session, (uint)packet.Length);
        if (buffer == 0)
        {
            throw new IOException($"虚拟网卡 {Name} 发送队列已满，丢弃 {packet.Length} 字节");
        }

        // Marshal.Copy 只能从托管数组取数，Ring 缓冲的写入必须先把包复制成数组
        var scratch = ArrayPool<byte>.Shared.Rent(packet.Length);
        try
        {
            packet.CopyTo(scratch.AsMemory(0, packet.Length));
            Marshal.Copy(scratch, 0, buffer, packet.Length);
            Native.SendPacket(session, buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken token)
        => await packets.Reader.WaitToReadAsync(token) && packets.Reader.TryRead(out var packet)
            ? Copy(packet, buffer)
            : 0;

    private static int Copy(byte[] packet, Memory<byte> buffer)
    {
        packet.CopyTo(buffer);
        return packet.Length;
    }

    /// <summary>轮询读事件，每次把环形缓冲里已有的包全部取走；队列满时由 Channel 直接丢弃新包。</summary>
    private void ReadLoop()
    {
        while (!stopped)
        {
            var wait = Native.WaitForSingleObject(readEvent, WaitMilliseconds);
            if (stopped)
            {
                break;
            }

            if (wait != WaitEvent0 && wait != WaitTimeout)
            {
                break;
            }

            while (true)
            {
                var packet = Native.ReceivePacket(session, out var size);
                if (packet == 0)
                {
                    break;
                }

                try
                {
                    var buffer = new byte[size];
                    Marshal.Copy(packet, buffer, 0, (int)size);
                    packets.Writer.TryWrite(buffer);
                }
                finally
                {
                    Native.ReleaseReceivePacket(session, packet);
                }
            }
        }

        packets.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Dispose()
    {
        stopped = true;
        packets.Writer.TryComplete();
        if (session != 0)
        {
            Native.CloseSession(session);
            session = 0;
        }

        if (adapter != 0)
        {
            Native.CloseAdapter(adapter);
            adapter = 0;
        }
    }

    private static class Native
    {
        [DllImport(Library, EntryPoint = "WintunCreateSession", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateSession(string name, string description, string? area);

        [DllImport(Library, EntryPoint = "WintunCreateAdapter", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateAdapter(nint wintun, string name, string tunnel, nint proposedLuid);

        [DllImport(Library, EntryPoint = "WintunOpenAdapter", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint OpenAdapter(string name);

        [DllImport(Library, EntryPoint = "WintunStartSession", SetLastError = true)]
        internal static extern nint StartSession(nint adapter, uint capacity);

        [DllImport(Library, EntryPoint = "WintunGetReadWaitEvent", SetLastError = true)]
        internal static extern nint GetReadWaitEvent(nint session);

        [DllImport(Library, EntryPoint = "WintunReceivePacket", SetLastError = true)]
        internal static extern nint ReceivePacket(nint session, out uint size);

        [DllImport(Library, EntryPoint = "WintunReleaseReceivePacket")]
        internal static extern void ReleaseReceivePacket(nint session, nint packet);

        [DllImport(Library, EntryPoint = "WintunAllocateSendPacket", SetLastError = true)]
        internal static extern nint AllocateSendPacket(nint session, uint size);

        [DllImport(Library, EntryPoint = "WintunSendPacket")]
        internal static extern void SendPacket(nint session, nint packet);

        [DllImport(Library, EntryPoint = "WintunCloseSession")]
        internal static extern void CloseSession(nint session);

        [DllImport(Library, EntryPoint = "WintunCloseAdapter")]
        internal static extern void CloseAdapter(nint adapter);

        [DllImport(Library, EntryPoint = "WintunCloseWintun")]
        internal static extern void CloseWintun(nint wintun);

        [DllImport("kernel32", EntryPoint = "WaitForSingleObject")]
        internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    }
}
