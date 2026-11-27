using System.Globalization;
using FastTunnel.Core.Config;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Security;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Options;

namespace FastTunnel.Server;

/// <summary>
/// 签发设备入网码。台账文件是服务端进程持有的状态，这里先合并磁盘上的外部改动再写，
/// 避免 CLI 与服务端交替写入时互相覆盖。
/// </summary>
internal static class PeerEnrollCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3
            || (args.Length == 3 && !TryMinutes(args[2], out _)))
        {
            Console.Error.WriteLine("用法: p2p-enroll <设备名> [有效分钟数]");
            return 2;
        }

        using var host = Program.CreateHostBuilder([]).Build();
        var config = host.Services.GetRequiredService<IOptionsMonitor<DefaultServerConfig>>().CurrentValue;
        if (!config.EnableP2P)
        {
            Console.Error.WriteLine("请先开启 FastTunnel:EnableP2P，入网设备需要 P2P 信令与中继。");
            return 2;
        }

        if (string.IsNullOrEmpty(config.P2PInviteKey))
        {
            Console.Error.WriteLine("请先配置 FastTunnel:P2PInviteKey（可用 p2p-invite-key 生成）。");
            return 2;
        }

        var registry = host.Services.GetRequiredService<PeerRegistry>();
        var minutes = args.Length == 3 ? int.Parse(args[2], CultureInfo.InvariantCulture) : config.P2PEnrollment.EnrollMinutes;
        if (!TryMinutes(minutes, out _))
        {
            Console.Error.WriteLine($"有效分钟数应为 1–{PeerEnrollment.MaxMinutes}。");
            return 2;
        }

        registry.RefreshFromDisk();
        var issued = registry.CreateTicket(config.P2PInviteKey, args[1], minutes);
        var fingerprint = TryFingerprint(host.Services);

        Console.WriteLine($"设备名   {args[1]}");
        Console.WriteLine($"入网码   {issued.Code}");
        Console.WriteLine($"失效时间 {issued.Expires:yyyy-MM-dd HH:mm:ss} UTC（单次使用）");
        Console.WriteLine("把上面两行交给设备侧执行：");
        Console.WriteLine($"  FastTunnel.Client p2p-enroll https://<节点地址:端口> {issued.Code}{(fingerprint == null ? "" : " " + fingerprint)}");
        Console.WriteLine("「节点地址:端口」是服务端对外 HTTPS 地址，与浏览器访问的同一个端口。");
        if (fingerprint == null)
        {
            Console.WriteLine("未取到服务端 TLS 证书指纹，可在服务端启动日志里找到并追加为最后一个参数。");
        }

        if (registry.StateFilePath != null)
        {
            Console.WriteLine($"台账文件 {registry.StateFilePath}");
        }

        return 0;
    }

    private static string? TryFingerprint(IServiceProvider services)
    {
        try
        {
            var provider = services.GetRequiredService<ServerCertificateProvider>();
            return provider.Options.Enabled ? TunnelCertificate.GetFingerprint(provider.GetCertificate()) : null;
        }
        catch (Exception)
        {
            // 证书还没准备好（例如首次运行在工作目录外执行），不影响入网码本身可用
            return null;
        }
    }

    private static bool TryMinutes(int minutes, out int value)
    {
        value = minutes;
        return minutes is >= 1 and <= PeerEnrollment.MaxMinutes;
    }

    private static bool TryMinutes(string value, out int minutes)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
           && minutes is >= 1 and <= PeerEnrollment.MaxMinutes;
}
