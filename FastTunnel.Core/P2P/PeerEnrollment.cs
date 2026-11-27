using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FastTunnel.Core.Security;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 设备入网码：管理员签发、客户端一次性兑换成长期凭据。
/// 码本身自包含（HMAC + 截止时间），服务端只需保存码指纹即可判定重复使用。
/// </summary>
public static class PeerEnrollment
{
    private const string Prefix = "fte1.";
    /// <summary>入网码默认有效分钟数。</summary>
    public const int DefaultMinutes = 15;

    /// <summary>入网码最长有效期。</summary>
    public const int MaxMinutes = 60;

    private const int MaxCodeLength = 200;

    /// <summary>为新建的待用记录签发入网码，签名密钥复用 <c>FastTunnel:P2PInviteKey</c>。</summary>
    public static string Create(string key, Guid ticket, int minutes)
    {
        if (ticket == Guid.Empty)
            throw new ArgumentException("入网码需要一个非空票据编号", nameof(ticket));
        if (minutes is < 1 or > MaxMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), $"入网码有效期应为 1–{MaxMinutes} 分钟");
        Span<byte> secret = stackalloc byte[32];
        if (!DeriveKey(key, secret))
            throw new ArgumentException("FastTunnel:P2PInviteKey 应为 64 位十六进制随机密钥", nameof(key));

        try
        {
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{Prefix}{ticket:N}.{DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds()}");
            return text + "." + Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(text)));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>校验入网码签名与截止时间，取出票据编号。</summary>
    internal static (Guid Ticket, DateTimeOffset Expires)? Read(string? code, string? key, DateTimeOffset now)
    {
        if (code is not { Length: > 0 and <= MaxCodeLength } || !code.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var fields = code.Split('.', 4);
        if (fields.Length != 4 || !Guid.TryParseExact(fields[1], "N", out var ticket) || ticket == Guid.Empty
            || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires is <= 0 or > 253402300799L || fields[3].Length != 64 || !fields[3].All(char.IsAsciiHexDigit))
            return null;

        var deadline = DateTimeOffset.FromUnixTimeSeconds(expires);
        if (deadline <= now || deadline > now.AddMinutes(MaxMinutes))
            return null;

        Span<byte> secret = stackalloc byte[32];
        if (!DeriveKey(key, secret))
            return null;
        try
        {
            Span<byte> received = stackalloc byte[32];
            if (!PeerInvites.ReadKey(fields[3], received))
                return null;
            var expected = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(code[..code.LastIndexOf('.')]));
            return CryptographicOperations.FixedTimeEquals(received, expected) ? (ticket, deadline) : null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>入网码指纹，服务端只存这个，泄露状态文件也无法兑换。</summary>
    internal static string Fingerprint(string code) => BanRegistry.Fingerprint(code);

    private static bool DeriveKey(string? key, Span<byte> secret)
    {
        Span<byte> root = stackalloc byte[32];
        if (!PeerInvites.ReadKey(key, root))
            return false;
        try
        {
            var rootBytes = root.ToArray();
            try
            {
                var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootBytes, secret.Length,
                    "FastTunnel/enroll/1"u8.ToArray());
                derived.CopyTo(secret);
                CryptographicOperations.ZeroMemory(derived);
            }
            finally { CryptographicOperations.ZeroMemory(rootBytes); }
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(root); }
    }
}
