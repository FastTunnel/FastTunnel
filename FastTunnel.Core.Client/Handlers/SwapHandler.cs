// Copyright (c) 2019-2022 Gui.H. https://github.com/FastTunnel/FastTunnel
// The FastTunnel licenses this file to you under the Apache License Version 2.0.
// For more details,You may obtain License file at: https://github.com/FastTunnel/FastTunnel/blob/v2/LICENSE

using FastTunnel.Core.Client;
using FastTunnel.Core.Client.Sockets;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Handlers.Client
{
    public class SwapHandler : IClientHandler
    {
        private const int MaxDatagram = 65535;

        readonly ILogger<SwapHandler> _logger;
        static int connectionCount;

        public SwapHandler(ILogger<SwapHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandlerMsgAsync(FastTunnelClient cleint, string msg, CancellationToken cancellationToken)
        {
            // Forward (UDP):  requestId|udp|host:port
            // Forward (TCP) / SwapMsg (web): requestId|host:port
            var msgs = msg.Split('|');
            var requestId = msgs[0];

            bool isUdp = msgs.Length >= 3 && string.Equals(msgs[1], "udp", StringComparison.OrdinalIgnoreCase);
            var address = isUdp ? msgs[2] : msgs[1];

            if (isUdp)
            {
                await swapUdp(cleint, requestId, address, cancellationToken);
            }
            else
            {
                await swap(cleint, requestId, address, cancellationToken);
            }
        }

        private async Task swap(FastTunnelClient cleint, string requestId, string address, CancellationToken cancellationToken)
        {
            try
            {
                Interlocked.Increment(ref connectionCount);
                _logger.LogDebug($"======Swap {requestId} Start======");
                using (Stream serverStream = await createRemote(requestId, cleint, cancellationToken))
                using (Stream localStream = await createLocal(requestId, address, cancellationToken))
                {
                    var taskX = serverStream.CopyToAsync(localStream, cancellationToken);
                    var taskY = localStream.CopyToAsync(serverStream, cancellationToken);

                    await Task.WhenAny(taskX, taskY);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Swap error {requestId}");
            }
            finally
            {
                Interlocked.Decrement(ref connectionCount);
                _logger.LogDebug($"======Swap {requestId} End======");
                _logger.LogDebug($"统计SwapHandler连接数：{connectionCount}");
            }
        }

        /// <summary>
        /// Bridge a UDP local target with the server tunnel using a length-prefixed framing.
        /// </summary>
        private async Task swapUdp(FastTunnelClient cleint, string requestId, string address, CancellationToken cancellationToken)
        {
            UdpClient udp = null;
            try
            {
                Interlocked.Increment(ref connectionCount);
                _logger.LogDebug($"======UDP-Swap {requestId} Start {address}======");

                using Stream serverStream = await createRemote(requestId, cleint, cancellationToken);

                var parts = address.Split(':');
                var host = parts[0];
                var port = int.Parse(parts[1]);

                IPAddress ipAddress;
                if (!IPAddress.TryParse(host, out ipAddress))
                {
                    var entries = await Dns.GetHostAddressesAsync(host);
                    ipAddress = entries.Length > 0 ? entries[0] : IPAddress.Loopback;
                }
                var localTarget = new IPEndPoint(ipAddress, port);

                udp = new UdpClient(0); // ephemeral local port
                udp.Connect(localTarget);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = linkedCts.Token;

                // Tunnel -> local UDP target: read [len][payload], send to localTarget.
                var t1 = Task.Run(async () =>
                {
                    var header = new byte[2];
                    var pool = ArrayPool<byte>.Shared;
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            if (!await ReadExactAsync(serverStream, header, 0, 2, token)) break;
                            int len = BinaryPrimitives.ReadUInt16BigEndian(header);
                            if (len == 0) continue;

                            var buf = pool.Rent(len);
                            try
                            {
                                if (!await ReadExactAsync(serverStream, buf, 0, len, token)) break;
                                await udp.SendAsync(buf, len);
                            }
                            finally
                            {
                                pool.Return(buf);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.LogDebug($"UDP recv-from-tunnel ended: {ex.Message}"); }
                });

                // Local UDP target -> tunnel: receive datagrams, write [len][payload].
                var t2 = Task.Run(async () =>
                {
                    var header = new byte[2];
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var result = await udp.ReceiveAsync();
                            var data = result.Buffer;
                            if (data.Length == 0 || data.Length > MaxDatagram) continue;
                            BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)data.Length);
                            await serverStream.WriteAsync(header, 0, 2, token);
                            await serverStream.WriteAsync(data, 0, data.Length, token);
                            await serverStream.FlushAsync(token);
                        }
                    }
                    catch (ObjectDisposedException) { }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.LogDebug($"UDP send-to-tunnel ended: {ex.Message}"); }
                });

                await Task.WhenAny(t1, t2);
                linkedCts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UDP-Swap error {requestId}");
            }
            finally
            {
                try { udp?.Close(); } catch { }
                Interlocked.Decrement(ref connectionCount);
                _logger.LogDebug($"======UDP-Swap {requestId} End======");
                _logger.LogDebug($"统计SwapHandler连接数：{connectionCount}");
            }
        }

        private async Task<Stream> createLocal(string requestId, string localhost, CancellationToken cancellationToken)
        {
            var socket = await DnsSocketFactory.ConnectAsync(localhost.Split(":")[0], int.Parse(localhost.Split(":")[1]));
            return new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };
        }

        private async Task<Stream> createRemote(string requestId, FastTunnelClient cleint, CancellationToken cancellationToken)
        {
            var socket = await DnsSocketFactory.ConnectAsync(cleint.Server.ServerAddr, cleint.Server.ServerPort);
            Stream serverStream = new NetworkStream(socket, true) { ReadTimeout = 1000 * 60 * 10 };

            if (cleint.Server.Protocol == "wss")
            {
                var sslStream = new SslStream(serverStream, false, delegate { return true; });
                await sslStream.AuthenticateAsClientAsync(cleint.Server.ServerAddr);
                serverStream = sslStream;
            }

            var reverse = $"PROXY /{requestId} HTTP/1.1\r\nHost: {cleint.Server.ServerAddr}:{cleint.Server.ServerPort}\r\n\r\n";
            var requestMsg = Encoding.UTF8.GetBytes(reverse);
            await serverStream.WriteAsync(requestMsg, cancellationToken);
            return serverStream;
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, offset + read, count - read, token);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
