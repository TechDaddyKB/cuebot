using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Kick.Auth;

/// <summary>PKCE (RFC 7636) values for one authorization request: verifier, S256 challenge and a state nonce.</summary>
internal sealed record PkceRequest(string Verifier, string Challenge, string State)
{
    public const string Method = "S256";

    public static PkceRequest Create() => From(RandomToken(64), RandomToken(32));

    /// <summary>Builds the challenge for a known verifier (tests use fixed values).</summary>
    public static PkceRequest From(string verifier, string state)
    {
        if (verifier.Length is < 43 or > 128)
            throw new ArgumentException("A PKCE verifier must be 43 to 128 characters long", nameof(verifier));
        return new PkceRequest(verifier, ChallengeFor(verifier), state);
    }

    /// <summary>BASE64URL(SHA256(ASCII(verifier))) without padding.</summary>
    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A URL-safe random string from <paramref name="bytes"/> random bytes (unreserved characters only).</summary>
    public static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
