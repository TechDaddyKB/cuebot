using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Obs.Protocol;

/// <summary>Computes the obs-websocket v5 authentication string.</summary>
public static class ObsAuth
{
    /// <summary>
    /// Returns <c>base64(sha256(base64(sha256(password + salt)) + challenge))</c>, the value sent in the
    /// <c>authentication</c> field of the Identify message.
    /// </summary>
    public static string Compute(string password, string salt, string challenge)
    {
        var secret = Base64Sha256(password + salt);
        return Base64Sha256(secret + challenge);
    }

    private static string Base64Sha256(string text) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
