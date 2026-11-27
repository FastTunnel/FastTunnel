using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using FastTunnel.Core.Config;
using FastTunnel.Core.Protocol;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 向协调节点领取 L3 组网目录。走 P2P 信令的同一个端口、同一种 Token 头和同一套证书规则，
/// 因此不需要额外放行端口，也不会绕过用户配置的证书指纹。
/// 只有 <see cref="PeerOptions.EnableL3"/> 打开时才会被创建。
/// </summary>
internal sealed class PeerNetworkDirectory(SuiDaoServer server, string credential, ILogger logger) : IDisposable
{
    private readonly Uri address = new($"{(server.UseTls ? "https" : "http")}://{server.ServerAddr}:{server.ServerPort}"
        + PeerProtocol.NetworkPath);
    private readonly HttpClient http = new(Pin(server, logger)) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>服务端没给缓存头时的兜底刷新周期。</summary>
    private static readonly TimeSpan DefaultRefresh = TimeSpan.FromSeconds(60);

    /// <summary>下次刷新目录的间隔，取服务端 <c>Cache-Control: max-age</c>。</summary>
    internal TimeSpan RefreshAfter { get; private set; } = DefaultRefresh;

    /// <summary>服务端明确表示组网用不了（未开启、没走 TLS、凭据不在台账），再重试也没有意义。</summary>
    internal bool Blocked { get; private set; }

    internal async Task<PeerNetworkRoster?> FetchAsync(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Add(ProtocolConst.FASTTUNNEL_TOKEN, credential);
        try
        {
            using var answer = await http.SendAsync(request, cancellation);
            if (!answer.IsSuccessStatusCode)
            {
                Blocked = answer.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                    or HttpStatusCode.Forbidden;
                logger.LogWarning("从 {Node} 领取组网目录失败：{Reason}", address.Host, Explain(answer.StatusCode));
                return null;
            }

            var roster = await answer.Content.ReadFromJsonAsync(AppJsonContext.Default.PeerNetworkRoster, cancellation);
            if (roster == null || PeerNetworkCidr.Parse(roster.Cidr) == null || roster.Peers.Length == 0
                || Array.Find(roster.Peers, entry => string.Equals(entry.Id, roster.Self, StringComparison.OrdinalIgnoreCase)) == null)
            {
                logger.LogWarning("节点 {Node} 返回的组网目录不完整，本次忽略", address.Host);
                return null;
            }

            Blocked = false;
            RefreshAfter = answer.Headers.CacheControl?.MaxAge is { } maxAge
                ? TimeSpan.FromSeconds(Math.Clamp(maxAge.TotalSeconds, 10, 3600))
                : DefaultRefresh;
            return roster;
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning("无法从 {Node} 领取组网目录：{Message}", address.Host, ex.Message);
            return null;
        }
    }

    /// <summary>沿用 P2P 信令的证书规则：固定指纹优先，其次系统 CA，最后才允许显式跳过校验。</summary>
    private static HttpClientHandler Pin(SuiDaoServer server, ILogger logger)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        if (server.UseTls)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                TunnelCertificate.Validate(certificate, errors, server, logger);
        }

        return handler;
    }

    /// <summary>把服务端状态码翻译成用户能直接照做的配置项。</summary>
    private static string Explain(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "服务端未开启 FastTunnel:P2PNetwork:Enabled，L3 组网不可用",
        HttpStatusCode.BadRequest => "组网目录必须通过 TLS 获取，请把 FastTunnel:Server:Protocol 改为 wss",
        HttpStatusCode.Unauthorized => "Token 无效或已被封禁",
        HttpStatusCode.Forbidden => "此凭据不在入网台账里，请用 p2p-enroll 入网后领取虚拟地址",
        HttpStatusCode.ServiceUnavailable => "服务端 FastTunnel:P2PNetwork:Cidr 无效或网段已分完",
        _ => $"HTTP {(int)status}",
    };

    public void Dispose()
    {
        http.Dispose();
    }
}
