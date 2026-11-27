using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

internal sealed class PeerAttemptLog(ILogger logger, string transport, string service,
    bool visitor, int coordinator, int attempt)
{
    private readonly long started = Environment.TickCount64;
    internal string Stage = "signaling";
    internal int Local;
    internal int Discovered;
    internal int Mapped;
    internal int Remote;
    internal int DiscoveryTargets;
    internal int ProbeReplies;
    internal int CommitTimeouts;
    internal int TcpConnectFailures;
    internal int TcpTlsFailures;
    internal string? TcpError;

    internal void Failed(Exception error)
    {
        var reason = error is PeerPathException failure ? failure.Reason : error switch
        {
            OperationCanceledException when Stage == "signaling" => "signaling_timeout",
            OperationCanceledException => "attempt_timeout",
            System.Threading.Channels.ChannelClosedException => "signaling_closed",
            System.Security.Authentication.AuthenticationException => "authentication_failed",
            System.Security.Cryptography.CryptographicException => "invalid_peer_key",
            System.Net.Sockets.SocketException => "socket_error",
            _ => "protocol_or_io_error"
        };

        // Exception messages from sockets, certificates and remote signaling may contain addresses.
        logger.Log(reason == "no_tcp_candidates" ? LogLevel.Debug : LogLevel.Information,
            "P2P 直连失败 {Transport} {Service} {Role} 协调节点序号={Coordinator} 尝试={Attempt} 阶段={Stage} 原因={Reason} 耗时毫秒={ElapsedMs} 本地候选={LocalCandidates} 地址发现目标={DiscoveryTargets} 发现候选={DiscoveredCandidates} 映射候选={MappedCandidates} 对端候选={RemoteCandidates} 探测响应={ProbeReplies} 确认超时={CommitTimeouts} TCP连接失败={TcpConnectFailures} TLS失败={TcpTlsFailures} TCP错误码={TcpError}",
            transport, service, visitor ? "visitor" : "provider", coordinator, attempt, Stage, reason,
            Environment.TickCount64 - started, Local, DiscoveryTargets, Discovered, Mapped, Remote, ProbeReplies,
            CommitTimeouts, TcpConnectFailures, TcpTlsFailures, TcpError);
    }
}

internal sealed class PeerPathException(string reason) : IOException(reason)
{
    internal string Reason { get; } = reason;
}
