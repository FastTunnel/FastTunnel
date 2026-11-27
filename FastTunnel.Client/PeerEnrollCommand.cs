using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FastTunnel.Core.Config;
using FastTunnel.Core.Tls;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FastTunnel.Client;

/// <summary>
/// 用管理员签发的入网码把这台设备注册到服务端，凭据写进客户端工作目录的
/// fasttunnel-peer.json。该文件在配置源里优先级最低，显式 appsettings 仍然胜出。
/// </summary>
internal static class PeerEnrollCommand
{
    internal const string CredentialFileName = "fasttunnel-peer.json";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["p2p-enroll", "--help" or "-h"])
        {
            PrintHelp();
            return 0;
        }

        if (args.Length is < 3 or > 4
            || !TryServer(args[1], out var server)
            || !args[2].StartsWith("fte1.", StringComparison.Ordinal) || args[2].Length > 200
            || (args.Length == 4 && !IsFingerprint(args[3])))
        {
            Console.Error.WriteLine("入网参数不正确。");
            PrintHelp();
            return 2;
        }

        if (args.Length == 4)
        {
            server.CertificateFingerprint = args[3];
        }

        // NativeAOT 下不能反射序列化匿名对象，直接用节点树拼请求体
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["code"] = args[2],
            ["hostname"] = Dns.GetHostName(),
        }.ToJsonString());

        using var handler = new HttpClientHandler();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
        var certificateLogger = loggerFactory.CreateLogger("enroll");
        handler.ServerCertificateCustomValidationCallback =
            (_, certificate, chain, errors) => TunnelCertificate.Validate(certificate, errors, server, certificateLogger);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var url = $"https://{server.ServerAddr}:{server.ServerPort}/api/Peers/Enroll";
        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();
            if (!TryReadCredential(body, out var id, out var name, out var token, out var error))
            {
                Console.Error.WriteLine($"入网失败：{error ?? $"服务端返回 {(int)response.StatusCode}"}");
                return 1;
            }

            WriteCredential(id, name, token, server);
            Console.WriteLine($"入网成功：主体 {id}（设备名 {name}）");
            Console.WriteLine($"凭据已写入 {Path.GetFullPath(CredentialFileName)}，权限 0600，请勿外传。");
            Console.WriteLine("直接运行 FastTunnel.Client 即可接入；要共享本机服务再配置 FastTunnel:P2P:Share。");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"无法连接 {url}：{ex.Message}");
            if (string.IsNullOrEmpty(server.CertificateFingerprint))
            {
                Console.Error.WriteLine("自签证书请用第 3 个参数固定服务端 SHA-256 指纹（服务端启动日志里有）。");
            }

            return 1;
        }
    }

    /// <summary>只接受 wss/https 地址：入网码是秘密，不能走明文。</summary>
    private static bool TryServer(string value, out SuiDaoServer server)
    {
        server = new SuiDaoServer();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("wss" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0
            || uri.AbsolutePath is not ("/" or "") || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        var port = uri.Port > 0 ? uri.Port : 443;
        server.Protocol = "wss";
        server.ServerAddr = uri.DnsSafeHost;
        server.ServerPort = port;
        server.CertificateFingerprint = "";
        return true;
    }

    private static bool IsFingerprint(string value)
    {
        var cleaned = value.Replace(":", "").Replace("-", "");
        return cleaned.Length == 64 && cleaned.All(char.IsAsciiHexDigit);
    }

    private static bool TryReadCredential(
        string body, out string id, out string name, out string token, out string? error)
    {
        id = name = token = "";
        error = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "响应格式不正确";
                return false;
            }

            if (root.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            {
                error = root.TryGetProperty("errorMessage", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : "服务端拒绝入网";
                return false;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("token", out var tokenNode) || tokenNode.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String)
            {
                error = "响应缺少凭据字段";
                return false;
            }

            token = tokenNode.GetString()!;
            id = idNode.GetString()!;
            name = data.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()!
                : id;
            return token.Length > 0 && id.Length > 0;
        }
        catch (JsonException)
        {
            error = "响应不是合法 JSON";
            return false;
        }
    }

    /// <summary>
    /// 把入网凭据摊平成配置键。文件损坏或结构不对时返回 null，让启动退回原有配置，
    /// 而不是带着半截凭据连不上服务端。
    /// </summary>
    internal static IReadOnlyDictionary<string, string?>? ReadCredential(out string? path)
    {
        path = null;
        if (!File.Exists(CredentialFileName))
            return null;

        path = Path.GetFullPath(CredentialFileName);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("FastTunnel", out var tunnel)
                || tunnel.ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine($"{path} 里没有 FastTunnel 配置段，已忽略。");
                return null;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            Flatten("FastTunnel", tunnel, values);
            if (!values.ContainsKey("FastTunnel:Token"))
            {
                Console.Error.WriteLine($"{path} 缺少 Token，已忽略。");
                return null;
            }

            return values;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{path} 读取失败（{ex.Message}），本次启动忽略入网凭据。");
            return null;
        }
    }

    private static void Flatten(string prefix, JsonElement element, Dictionary<string, string?> values)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix + ":" + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(key, property.Value, values);
                    break;
                case JsonValueKind.String:
                    values[key] = property.Value.GetString();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    values[key] = property.Value.GetBoolean() ? "true" : "false";
                    break;
                case JsonValueKind.Number:
                    values[key] = property.Value.GetRawText();
                    break;
            }
        }
    }

    private static void WriteCredential(string id, string name, string token, SuiDaoServer server)
    {
        var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var file = new FileStream(CredentialFileName, options);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("EnrolledAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("Subject", id);
        json.WriteString("Device", name);
        json.WriteStartObject("FastTunnel");
        json.WriteString("Token", token);
        json.WriteStartObject("Server");
        json.WriteString("Protocol", server.Protocol);
        json.WriteString("ServerAddr", server.ServerAddr);
        json.WriteNumber("ServerPort", server.ServerPort);
        if (server.CertificateFingerprint.Length > 0)
            json.WriteString("CertificateFingerprint", server.CertificateFingerprint);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("用管理员签发的入网码把本机注册到 FastTunnel 服务端。");
        Console.WriteLine("p2p-enroll <https://节点地址:端口> <入网码 fte1.…> [证书 SHA-256 指纹]");
        Console.WriteLine("成功后生成 fasttunnel-peer.json，之后直接运行 FastTunnel.Client 即可。");
    }
}
