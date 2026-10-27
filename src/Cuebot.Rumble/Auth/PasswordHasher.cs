using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Rumble.Auth;

/// <summary>
/// Rumble's login does not send the password itself but MD5 "stretched" hashes of it, computed with three salts
/// the server hands out per username (unofficial, see PROTOCOL.md). This is Rumble's scheme, not a security
/// choice of Cuebot's.
/// </summary>
internal static class PasswordHasher
{
    private const int Rounds = 128;

    /// <summary>The <c>password_hashes</c> form value: "hash1,hash2,salt2".</summary>
    public static string Hash(string password, IReadOnlyList<string> salts)
    {
        if (salts.Count < 3) throw new RumbleApiException("Rumble's login answered with an unexpected format.");
        var first = Md5Hex(Stretch(password, salts[0]) + salts[1]);
        var second = Stretch(password, salts[2]);
        return string.Join(',', first, second, salts[1]);
    }

    /// <summary>md5(salt + password), then <see cref="Rounds"/> times md5(previous + password).</summary>
    internal static string Stretch(string password, string salt)
    {
        var current = Md5Hex(salt + password);
        for (var i = 0; i < Rounds; i++) current = Md5Hex(current + password);
        return current;
    }

#pragma warning disable CA5351 // MD5 is mandated by Rumble's login protocol.
    internal static string Md5Hex(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351
}
