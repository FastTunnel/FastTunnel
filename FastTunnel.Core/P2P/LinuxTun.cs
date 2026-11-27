using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FastTunnel.Core.P2P;

/// <summary>
/// Linux 的 <c>/dev/net/tun</c> 虚拟网卡。建好后用 <c>ip</c> 命令配置地址，
/// 读取放在独立后台线程里，避免长时间占用线程池，也不会让退出流程卡在阻塞读上。
/// </summary>
internal sealed class LinuxTun : ITunDevice
{
    private const string Library = "libc";
    private const int OpenReadWrite = 2;
    private const int NameSize = 16;
    private const uint TunSetIff = 0x400454CA;
    private const ushort IfTun = 0x0001;
    private const ushort IfNoPi = 0x1000;
    private const int OperationNotPermitted = 1;

    private readonly FileStream device;
    private readonly SafeFileHandle handle;
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly Thread reader;

    private LinuxTun(SafeFileHandle handle, FileStream device, string name)
    {
        this.handle = handle;
        this.device = device;
        Name = name;
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "FastTunnel TUN" };
    }

    public string Name { get; }

    /// <summary>创建并配置网卡；权限不足或命令失败时记录原因并返回 null，其余 P2P 功能不受影响。</summary>
    internal static LinuxTun? TryCreate(TunSetup setup, ILogger logger)
    {
        var fd = Native.open("/dev/net/tun", OpenReadWrite);
        if (fd < 0)
        {
            logger.LogWarning("无法打开 /dev/net/tun：{Reason}。L3 组网需要访问该设备", Error(Native.ioctlError()));
            return null;
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        var request = new IfReq { Name = new byte[NameSize], Flags = IfTun | IfNoPi };
        Encoding.ASCII.GetBytes(setup.Name, 0, Math.Min(setup.Name.Length, NameSize - 1), request.Name, 0);
        if (Native.ioctl(handle, TunSetIff, ref request) != 0)
        {
            var code = Native.ioctlError();
            handle.Dispose();
            logger.LogWarning("创建虚拟网卡 {Name} 失败：{Reason}。L3 组网需要 CAP_NET_ADMIN，请用 sudo 运行客户端或预先授予该能力",
                setup.Name, Error(code) + (code == OperationNotPermitted ? "（权限不足）" : ""));
            return null;
        }

        var tun = new LinuxTun(handle, new FileStream(handle, FileAccess.ReadWrite, Native.BufferSize), setup.Name);
        if (!tun.Configure(setup, logger))
        {
            tun.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }

        tun.reader.Start();
        logger.LogInformation("虚拟网卡 {Name} 已就绪：{Address}/{Prefix}，MTU {Mtu}",
            setup.Name, setup.Address, setup.PrefixLength, setup.Mtu);
        return tun;
    }

    /// <summary>依次设置 MTU、启用接口、写入虚拟地址；任何一步失败都放弃这块网卡。地址配好后再尝试接管解析。</summary>
    private bool Configure(TunSetup setup, ILogger logger)
    {
        if (Native.FindIp() is not { } ip)
        {
            logger.LogWarning("找不到 ip 命令，无法配置虚拟网卡 {Name}；请安装 iproute2，或手动执行 ip addr add {Address}/{Prefix} dev {Name}",
                setup.Name, setup.Address, setup.PrefixLength, setup.Name);
            return false;
        }

        foreach (var args in new string[][]
        {
            ["link", "set", setup.Name, "mtu", setup.Mtu.ToString()],
            ["link", "set", setup.Name, "up"],
            ["addr", "add", $"{setup.Address}/{setup.PrefixLength}", "dev", setup.Name],
        })
        {
            var (code, error) = TunProcess.Run(ip, args);
            if (code != 0)
            {
                logger.LogWarning("执行 ip {Command} 失败（退出码 {Code}）：{Error}。请确认客户端有 CAP_NET_ADMIN",
                    string.Join(' ', args), code, error.Length == 0 ? "无输出" : error);
                return false;
            }
        }

        if (setup.Dns)
        {
            ConfigureResolved(setup, logger);
        }

        return true;
    }

    /// <summary>
    /// 用 systemd-resolved 做分流解析：只把 <c>.ft</c> 后缀交给这块网卡，正常上网的域名照旧走原有 DNS。
    /// 机器上没有 resolvectl 时只提示，绝不去改 <c>/etc/resolv.conf</c>。
    /// </summary>
    private static void ConfigureResolved(TunSetup setup, ILogger logger)
    {
        var suffix = PeerSocks5.Suffix.TrimStart('.');
        if (TunProcess.Run("resolvectl", ["dns", setup.Name, setup.Address]).Code != 0)
        {
            logger.LogWarning("这台机器没有 systemd-resolved，无法把 {Name} 的解析指向 {Address}。" +
                "要用组网域名请自行配置解析，或关闭 FastTunnel:P2P:L3Dns", setup.Name, setup.Address);
            return;
        }

        var (code, error) = TunProcess.Run("resolvectl", ["domain", setup.Name, "~" + suffix]);
        if (code != 0)
        {
            logger.LogWarning("执行 resolvectl domain {Name} 失败：{Error}", setup.Name, error.Length == 0 ? "无输出" : error);
            return;
        }

        logger.LogInformation("组网域名已生效：查 *.{Suffix} 走 {Name} 上的 {Address}", suffix, setup.Name, setup.Address);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ReadCoreAsync(buffer, token);

    /// <summary>写进 /dev/net/tun 就是交给内核队列，没有可缓冲的尾部数据，因此不需要 flush。</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
        => device.WriteAsync(packet, token);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken token)
        => await packets.Reader.WaitToReadAsync(token) && packets.Reader.TryRead(out var packet)
            ? Copy(packet, buffer)
            : 0;

    private static int Copy(byte[] packet, Memory<byte> buffer)
    {
        packet.CopyTo(buffer);
        return packet.Length;
    }

    /// <summary>内核送来的包先进队列；队列满了直接丢弃，TCP 由内核自行重传。</summary>
    private void ReadLoop()
    {
        var scratch = new byte[Native.BufferSize];
        try
        {
            while (true)
            {
                var length = device.Read(scratch);
                if (length <= 0)
                {
                    break;
                }

                var packet = new byte[length];
                Array.Copy(scratch, packet, length);
                packets.Writer.TryWrite(packet);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            packets.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        packets.Writer.TryComplete();
        device.Dispose();
        handle.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Error(int errno) => $"errno {errno}";

    private static class Native
    {
        internal const int BufferSize = 64 * 1024;

        [DllImport(Library, EntryPoint = "open", SetLastError = true)]
        internal static extern int open(string pathname, int flags);

        [DllImport(Library, EntryPoint = "ioctl", SetLastError = true)]
        internal static extern int ioctl(SafeHandle fd, uint request, ref IfReq arg);

        internal static int ioctlError() => Marshal.GetLastPInvokeError();

        /// <summary>服务进程的 PATH 常常没有 /usr/sbin，逐个候选试跑一次 <c>ip link</c>。</summary>
        internal static string? FindIp()
        {
            foreach (var candidate in new[] { "ip", "/usr/sbin/ip", "/sbin/ip" })
            {
                if (TunProcess.Run(candidate, ["link"]).Code != -1)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>struct ifreq 的前两个字段：名字数组加标志位，后面的是联合里的其他成员，用不到。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IfReq
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NameSize)]
        internal byte[] Name;

        internal ushort Flags;
    }
}
