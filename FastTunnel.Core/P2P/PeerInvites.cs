using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FastTunnel.Core.P2P;

/// <summary>Issues short-lived paired credentials for one private P2P service.</summary>
public static class PeerInvites
{
    private const string Prefix = "ftpi1.";
    /// <summary>Maximum invitation lifetime in minutes.</summary>
    public const int MaxMinutes = 120;

    /// <summary>Issues separate provider and visitor credentials using the coordinator's hex signing key.</summary>
    public static (string ProviderId, string VisitorId, string ProviderToken, string VisitorToken, DateTimeOffset Expires)
        Create(string key, string service, int minutes)
    {
        if (!PeerProtocol.IsName(service))
            throw new ArgumentException("服务名只能包含字母、数字、点、下划线或连字符", nameof(service));
        if (minutes is < 1 or > MaxMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), "邀请有效期应为 1–120 分钟");
        Span<byte> secret = stackalloc byte[32];
        if (!ReadKey(key, secret))
            throw new ArgumentException("FastTunnel:P2PInviteKey 应为 64 位十六进制随机密钥", nameof(key));

        try
        {
            var id = Guid.NewGuid();
            var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds());
            var provider = new PeerInvite(id, service, expires, Provider: true);
            var visitor = provider with { Provider = false };
            return (provider.Id, visitor.Id, Sign(provider, secret), Sign(visitor, secret), expires);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static bool IsInvite(string? token) => token?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    internal static PeerInvite? Verify(string token, string? key, DateTimeOffset now)
    {
        if (!TryRead(token, out var invite, out var signature) || invite.Expires <= now
            || invite.Expires > now.AddMinutes(MaxMinutes))
            return null;

        Span<byte> secret = stackalloc byte[32];
        if (!ReadKey(key, secret))
            return null;
        try
        {
            Span<byte> received = stackalloc byte[32];
            if (!ReadKey(signature, received))
                return null;
            var expected = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(token[..token.LastIndexOf('.')]));
            return CryptographicOperations.FixedTimeEquals(received, expected) ? invite : null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static DateTimeOffset? ReadExpiry(string token)
        => TryRead(token, out var invite, out _) ? invite.Expires : null;

    private static bool TryRead(string token, out PeerInvite invite, out string signature)
    {
        invite = default;
        signature = "";
        if (token.Length > 190 || !IsInvite(token))
            return false;
        var fields = token.Split('.', 5);
        // The service can contain dots. Its signature is separated at the last dot.
        if (fields.Length != 5 || !Guid.TryParseExact(fields[1], "N", out var id) || id == Guid.Empty
            || fields[2] is not ("p" or "v")
            || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires is <= 0 or > 253402300799L)
            return false;
        var lastDot = fields[4].LastIndexOf('.');
        if (lastDot < 0 || !PeerProtocol.IsName(fields[4][..lastDot]) || fields[4].Length - lastDot - 1 != 64)
            return false;
        invite = new PeerInvite(id, fields[4][..lastDot], DateTimeOffset.FromUnixTimeSeconds(expires), fields[2] == "p");
        signature = fields[4][(lastDot + 1)..];
        return true;
    }

    internal static bool ReadKey(string? key, Span<byte> secret)
        => key is { Length: 64 } && Convert.FromHexString(key.AsSpan(), secret, out _, out var size) == OperationStatus.Done
            && size == 32;

    private static string Sign(PeerInvite invite, ReadOnlySpan<byte> secret)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{invite.Pair:N}.{(invite.Provider ? 'p' : 'v')}.{invite.Expires.ToUnixTimeSeconds()}.{invite.Service}");
        return text + "." + Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(text)));
    }
}

internal readonly record struct PeerInvite(Guid Pair, string Service, DateTimeOffset Expires, bool Provider)
{
    internal string ProviderId => $"test-{Pair:N}-p";
    internal string VisitorId => $"test-{Pair:N}-v";
    internal string Id => Provider ? ProviderId : VisitorId;
}

internal sealed record PeerIdentity(string Id, string Token, PeerInvite? Invite = null);
