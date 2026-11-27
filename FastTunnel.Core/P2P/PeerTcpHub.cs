using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using FastTunnel.Core.Sockets;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

internal sealed class PeerTcpHub : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<NetworkStream>> routes = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly Socket? listener;
    private readonly Task? accept;
    private readonly Lazy<X509Certificate2> certificate = new(CreateCertificate);

    internal PeerTcpHub(int port, ILogger logger)
    {
        if (port == 0)
            return;

        Socket? socket = null;
        try
        {
            socket = NetUtil.BindTcp(IPAddress.IPv6Any, port);
            socket.Listen(64);
            listener = socket;
            accept = AcceptAsync(socket, stopped.Token);
        }
        catch (SocketException ex)
        {
            socket?.Dispose();
            logger.LogWarning("P2P TCP 监听失败 端口={Port} Socket错误={SocketError}，仍可主动连接对端", port, ex.SocketErrorCode);
        }
    }

    internal X509Certificate2 Certificate => certificate.Value;
    internal int Port => (listener?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    internal ChannelReader<NetworkStream> Register(Guid route)
    {
        var pending = Channel.CreateBounded<NetworkStream>(8);
        if (!routes.TryAdd(route, pending))
            throw new InvalidOperationException("Duplicate P2P TCP route");
        return pending.Reader;
    }

    internal void Unregister(Guid route)
    {
        if (!routes.TryRemove(route, out var pending))
            return;

        pending.Writer.TryComplete();
        while (pending.Reader.TryRead(out var stream))
            stream.Dispose();
    }

    private async Task AcceptAsync(Socket socket, CancellationToken token)
    {
        var handoffs = new HashSet<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                handoffs.RemoveWhere(task => task.IsCompleted);
                if (handoffs.Count >= 64)
                    await Task.WhenAny(handoffs);
                var connection = await socket.AcceptAsync(token);
                handoffs.Add(DispatchAsync(connection, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(handoffs).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    private async Task DispatchAsync(Socket socket, CancellationToken token)
    {
        var stream = new NetworkStream(socket, ownsSocket: true);
        var transferred = false;
        try
        {
            socket.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var buffer = new byte[16];
            await stream.ReadExactlyAsync(buffer, timeout.Token);
            transferred = routes.TryGetValue(new Guid(buffer), out var pending) && pending.Writer.TryWrite(stream);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        finally
        {
            if (!transferred)
                stream.Dispose();
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=FastTunnel Peer", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));
        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        var pfx = generated.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public async ValueTask DisposeAsync()
    {
        stopped.Cancel();
        try
        {
            if (accept != null)
                await accept;
        }
        finally
        {
            foreach (var route in routes.Keys)
                Unregister(route);
            listener?.Dispose();
            if (certificate.IsValueCreated)
                certificate.Value.Dispose();
            stopped.Dispose();
        }
    }
}
