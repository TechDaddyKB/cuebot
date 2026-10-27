using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Server.StreamerBot;

/// <summary>
/// Streamer.bot's documented WebSocket authentication: the server sends a random salt and challenge in Hello; the
/// client answers <c>base64(sha256(base64(sha256(password + salt)) + challenge))</c> in an Authenticate request.
/// </summary>
public static class StreamerBotAuth
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Compute(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Constant-time comparison of a client's answer with the expected one.</summary>
    public static bool Verify(string password, string salt, string challenge, string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return false;
        var expected = Encoding.UTF8.GetBytes(Compute(password, salt, challenge));
        var given = Encoding.UTF8.GetBytes(answer);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
