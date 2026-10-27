using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Kick.Webhooks;

/// <summary>
/// Verifies Kick webhook signatures (docs.kick.com/events/webhook-security): the <c>Kick-Event-Signature</c>
/// header is a base64 RSA PKCS#1 v1.5 / SHA-256 signature, by Kick's public key, of
/// <c>{Kick-Event-Message-Id}.{Kick-Event-Message-Timestamp}.{raw body}</c>.
/// </summary>
public static class KickWebhookVerifier
{
    public static byte[] SignedPayload(string messageId, string timestamp, string body) =>
        Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.{body}");

    /// <summary>True only for a well-formed signature made with the private half of <paramref name="publicKeyPem"/>.</summary>
    public static bool Verify(string messageId, string timestamp, string body, string signatureBase64, string publicKeyPem)
    {
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatureBase64)) return false;
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(SignedPayload(messageId, timestamp, body), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
