using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Server.Api;

/// <summary>Bearer tokens for the REST API and MCP: random, URL-safe, compared in constant time.</summary>
public static class ApiToken
{
    public const string Prefix = "cb_";

    /// <summary>A new token: "cb_" + 32 random bytes, base64url.</summary>
    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// True when <paramref name="provided"/> equals <paramref name="expected"/>. Both are hashed first so the comparison
    /// takes the same time whatever their lengths; an empty expected token never matches.
    /// </summary>
    public static bool Matches(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || provided is null) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>The token of an "Authorization: Bearer ..." header value, or null.</summary>
    public static string? FromAuthorizationHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var text = header.Trim();
        const string scheme = "Bearer ";
        return text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? text[scheme.Length..].Trim() : null;
    }
}
