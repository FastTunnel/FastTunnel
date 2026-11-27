using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FastTunnel.Core.P2P;

internal static class PeerTcpConnection
{
    internal static async Task RunAsync(PeerTcpHub hub, PeerDirectControl.Attempt attempt, PeerWebSocket relay,
        IPEndPoint? publicEndpoint, bool visitor, PeerAttemptLog diagnostics,
        Func<PeerPipe, Task, Task> useTransport, CancellationToken token, CancellationToken directToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        var local = Guid.NewGuid();
        var incoming = hub.Register(local);
        var localEndpoints = hub.Port == 0 ? [] : PeerUdpHub.InterfaceAddresses()
            .Select(address => new IPEndPoint(address, hub.Port))
            .Prepend(publicEndpoint).OfType<IPEndPoint>().Distinct().Take(12).ToArray();
        diagnostics.Local = localEndpoints.Length;
        try
        {
            var prefix = "T" + attempt.Prefix;
            if (visitor)
                await relay.SendSignalAsync(prefix, deadline.Token);
            var fingerprint = hub.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var hello = string.Join('\n', local.ToString("N"), fingerprint, string.Join('\n', localEndpoints.Select(x => x.ToString())));
            await relay.SendSignalAsync(prefix + hello, deadline.Token);
            var remoteHello = await attempt.Signals.Reader.ReadAsync(deadline.Token);
            if (remoteHello.Length > 2048)
                throw new PeerPathException("tcp_invalid_offer");
            var fields = remoteHello.Split('\n');
            if (fields.Length is < 2 or > 14 || !Guid.TryParseExact(fields[0], "N", out var remote)
                || remote == local || fields[1].Length != 64 || !fields[1].All(char.IsAsciiHexDigit))
                throw new PeerPathException("tcp_invalid_offer");

            var trust = Convert.FromHexString(fields[1]);
            var remoteEndpoints = fields.Skip(2).Select(value => IPEndPoint.TryParse(value, out var endpoint) ? endpoint : null)
                .OfType<IPEndPoint>().Where(endpoint => endpoint.Port > 0 && PeerUdpHub.IsCandidate(endpoint.Address))
                .Distinct().ToArray();
            diagnostics.Remote = remoteEndpoints.Length;
            if (localEndpoints.Length == 0 && remoteEndpoints.Length == 0)
                throw new PeerPathException("no_tcp_candidates");

            diagnostics.Stage = "tcp_connect";
            var winner = new TaskCompletionSource<SslStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var selection = new SemaphoreSlim(1);
            var dialing = remoteEndpoints.Select(DialAsync).ToArray();
            var accepting = AcceptAsync();
            SslStream? active = null;
            try
            {
                active = await winner.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (diagnostics.TcpTlsFailures > 0)
                    throw new PeerPathException("tcp_tls_failed");
                if (diagnostics.TcpConnectFailures > 0)
                    throw new PeerPathException("tcp_connect_failed");
                throw new PeerPathException("tcp_connect_timeout");
            }
            finally
            {
                deadline.Cancel();
                await Task.WhenAll(dialing.Append(accepting)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (active == null && winner.Task.IsCompletedSuccessfully)
                    winner.Task.Result.Dispose();
            }

            await using (active)
            {
                directToken.ThrowIfCancellationRequested();
                diagnostics.Stage = "tcp_data";
                using var wireLifetime = CancellationTokenSource.CreateLinkedTokenSource(directToken);
                var pipe = new PeerPipe();
                var wire = pipe.RunAsync(async (writer, ct) =>
                {
                    while (true)
                    {
                        var count = await active.ReadAsync(writer.GetMemory(PeerProtocol.ChunkSize), ct);
                        if (count == 0)
                            return;
                        writer.Advance(count);
                        var flushed = await writer.FlushAsync(ct);
                        if (flushed.IsCompleted || flushed.IsCanceled)
                            return;
                    }
                }, (reader, ct) => reader.CopyToAsync(active, ct), wireLifetime.Token);
                try { await useTransport(pipe, wire); }
                finally
                {
                    wireLifetime.Cancel();
                    await wire.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }

            async Task DialAsync(IPEndPoint endpoint)
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                NetworkStream? stream = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.ConnectAsync(endpoint, timeout.Token);
                    stream = new NetworkStream(socket, ownsSocket: true);
                    await stream.WriteAsync(remote.ToByteArray(), timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    if (!deadline.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref diagnostics.TcpConnectFailures);
                        RecordError(ex);
                    }
                    stream?.Dispose();
                    socket.Dispose();
                    return;
                }
                await AuthenticateAsync(stream, inbound: false);
            }

            async Task AcceptAsync()
            {
                var pending = new HashSet<Task>();
                try
                {
                    await foreach (var stream in incoming.ReadAllAsync(deadline.Token))
                    {
                        pending.RemoveWhere(task => task.IsCompleted);
                        if (pending.Count < 8)
                            pending.Add(AuthenticateAsync(stream, inbound: true));
                        else
                            stream.Dispose();
                    }
                }
                finally { await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            }

            async Task AuthenticateAsync(NetworkStream network, bool inbound)
            {
                var ssl = new SslStream(network, leaveInnerStreamOpen: false,
                    (_, certificate, _, _) => certificate != null && CryptographicOperations.FixedTimeEquals(
                        certificate.GetCertHash(HashAlgorithmName.SHA256), trust),
                    (_, _, _, _, _) => hub.Certificate);
                var selected = false;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    if (inbound)
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = hub.Certificate,
                            ClientCertificateRequired = true
                        }, timeout.Token);
                    }
                    else
                    {
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                        {
                            TargetHost = "fasttunnel-peer",
                            ClientCertificates = new X509CertificateCollection { hub.Certificate }
                        }, timeout.Token);
                    }

                    var binding = new byte[32];
                    local.TryWriteBytes(binding);
                    remote.TryWriteBytes(binding.AsSpan(16));
                    await ssl.WriteAsync(binding, timeout.Token);
                    await ssl.ReadExactlyAsync(binding, timeout.Token);
                    if (new Guid(binding.AsSpan(0, 16)) != remote || new Guid(binding.AsSpan(16, 16)) != local)
                        throw new AuthenticationException("P2P TCP session mismatch");

                    var frame = new byte[1];
                    if (!visitor)
                    {
                        await ssl.ReadExactlyAsync(frame, timeout.Token);
                        if (frame[0] != 1)
                            return;
                    }

                    await selection.WaitAsync(timeout.Token);
                    try
                    {
                        if (winner.Task.IsCompleted)
                            return;
                        frame[0] = visitor ? (byte)1 : (byte)2;
                        await ssl.WriteAsync(frame, timeout.Token);
                        if (visitor)
                        {
                            await ssl.ReadExactlyAsync(frame, timeout.Token);
                            if (frame[0] != 2)
                                return;
                        }
                        selected = winner.TrySetResult(ssl);
                    }
                    finally { selection.Release(); }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException)
                {
                    if (!deadline.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref diagnostics.TcpTlsFailures);
                        RecordError(ex);
                    }
                }
                finally
                {
                    if (!selected)
                        ssl.Dispose();
                }
            }

            void RecordError(Exception error)
            {
                var cause = error.GetBaseException();
                diagnostics.TcpError = cause is System.ComponentModel.Win32Exception native
                    ? $"{cause.GetType().Name}:0x{native.NativeErrorCode:X8}"
                    : $"{cause.GetType().Name}:0x{cause.HResult:X8}";
            }
        }
        finally { hub.Unregister(local); }
    }
}
