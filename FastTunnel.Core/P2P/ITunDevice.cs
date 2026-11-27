namespace FastTunnel.Core.P2P;

/// <summary>
/// 虚拟网卡的最小读写面：只搬运完整的 IPv4 包，不掺手 TCP 状态。
/// 内核负责握手和重传，本端只做「收包—选路—发包」，所以不需要用户态协议栈。
/// </summary>
internal interface ITunDevice : IAsyncDisposable
{
    /// <summary>系统里的接口名，日志和排障用。</summary>
    internal string Name { get; }

    /// <summary>读出一个 IP 包，返回字节数；返回 0 表示网卡已关闭。</summary>
    internal ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token);

    /// <summary>把一个 IP 包交给内核。校验和由实现方决定是否补算。</summary>
    internal ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token);
}
