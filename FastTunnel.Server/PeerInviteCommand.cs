using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FastTunnel.Core.Config;
using FastTunnel.Core.P2P;
using Microsoft.Extensions.Options;

namespace FastTunnel.Server;

internal static class PeerInviteCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length is < 5 or > 7
            || !Uri.TryCreate(args[1], UriKind.Absolute, out var coordinator) || coordinator.Scheme != "wss"
            || coordinator.Host.Length == 0 || coordinator.Port is <= 0 or > 65535 || coordinator.AbsolutePath != "/"
            || coordinator.UserInfo.Length != 0 || coordinator.Query.Length != 0 || coordinator.Fragment.Length != 0
            || !TryNumber(args[3], 65535, out var servicePort)
            || !TryNumber(args[4], 65535, out var visitorPort)
            || (args.Length >= 6 && !TryNumber(args[5], PeerInvites.MaxMinutes, out _))
            || (args.Length == 7 && (args[6].Length != 64 || !args[6].All(char.IsAsciiHexDigit))))
        {
            Console.Error.WriteLine("用法: p2p-invite <wss://节点:端口> <服务名> <服务端口> <访客端口> [有效分钟数] [证书 SHA-256 指纹]");
            return 2;
        }

        using var host = Program.CreateHostBuilder([]).Build();
        var config = host.Services.GetRequiredService<IOptionsMonitor<DefaultServerConfig>>().CurrentValue;
        if (!config.EnableP2P || string.IsNullOrEmpty(config.P2PInviteKey))
        {
            Console.Error.WriteLine("请先配置 FastTunnel:EnableP2P 和 FastTunnel:P2PInviteKey。");
            return 2;
        }

        var service = args[2];
        var minutes = args.Length >= 6 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 30;
        var fingerprint = args.Length == 7 ? args[6] : null;
        var invite = PeerInvites.Create(config.P2PInviteKey, service, minutes);
        var output = Path.GetFullPath("p2p-invite-" + invite.ProviderId);
        var providerPath = Path.Combine(output, "provider");
        var visitorPath = Path.Combine(output, "visitor");
        CreatePrivateDirectory(output);
        CreatePrivateDirectory(providerPath);
        CreatePrivateDirectory(visitorPath);
        WriteClient(providerPath, invite.ProviderToken, "Share", service, $"{servicePort.ToString(CultureInfo.InvariantCulture)}@{invite.VisitorId}", null);
        WriteClient(visitorPath, invite.VisitorToken, "Connect", $"{invite.ProviderId}/{service}", null, visitorPort);
        Console.WriteLine($"邀请截止时间（UTC） {invite.Expires:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"提供端配置 {Path.Combine(providerPath, "appsettings.json")}");
        Console.WriteLine($"访客端配置 {Path.Combine(visitorPath, "appsettings.json")}");
        Console.WriteLine("把两份配置分别交给对应一端，放在客户端工作目录后启动 FastTunnel.Client。");
        Console.WriteLine($"访客连接地址 127.0.0.1:{visitorPort}");
        Console.WriteLine("访客日志中的「P2P TCP 连接使用」会显示实际路径。");
        return 0;

        void WriteClient(string directory, string token, string section, string name, string? value, int? port)
        {
            var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(Path.Combine(directory, "appsettings.json"), options);
            using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteStartObject("FastTunnel");
            json.WriteString("Token", token);
            json.WriteStartObject("Server");
            json.WriteString("Protocol", "wss");
            json.WriteString("ServerAddr", coordinator.DnsSafeHost);
            json.WriteNumber("ServerPort", coordinator.Port);
            if (fingerprint != null)
                json.WriteString("CertificateFingerprint", fingerprint);
            json.WriteEndObject();
            json.WriteStartObject("P2P");
            json.WriteBoolean("LogConnectionPath", true);
            json.WriteStartObject(section);
            if (port is { } localPort)
                json.WriteNumber(name, localPort);
            else
                json.WriteString(name, value);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
    }

    private static bool TryNumber(string value, int maximum, out int number)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0 && number <= maximum;

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
