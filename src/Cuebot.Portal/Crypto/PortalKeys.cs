using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Portal.Crypto;

/// <summary>
/// Encodings shared with the TypeScript side (portal/src/crypto.ts). P-256 public keys travel as raw uncompressed
/// points (65 bytes, 0x04 || X || Y), signatures as IEEE P1363 (r || s, 64 bytes), the same formats WebCrypto uses.
/// </summary>
public static class PortalKeys
{
    public const int PublicKeyLength = 65;
    public const int SignatureLength = 64;

    /// <summary>
    /// The relay ID of an identity key: lowercase base32 (RFC 4648, no padding) of the first 20 bytes of SHA-256 of
    /// the raw public key. 32 characters, safe in host names and paths, tied to no name or email.
    /// </summary>
    public static string RelayId(ReadOnlySpan<byte> publicKey) => Base32(SHA256.HashData(publicKey)[..20]);

    /// <summary>The fingerprint a device pins: base64url (no padding) of SHA-256 of the raw public key.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey) => Base64Url(SHA256.HashData(publicKey));

    public static byte[] RawPublicKey(ECParameters p)
    {
        var raw = new byte[PublicKeyLength];
        raw[0] = 0x04;
        p.Q.X!.CopyTo(raw, 1);
        p.Q.Y!.CopyTo(raw, 33);
        return raw;
    }

    /// <summary>The public point of a raw key; throws <see cref="CryptographicException"/> for anything malformed.</summary>
    public static ECParameters FromRawPublicKey(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != PublicKeyLength || raw[0] != 0x04) throw new CryptographicException("Not a raw P-256 public key.");
        // Checked here because platforms differ: Windows reports an off-curve point as PlatformNotSupportedException.
        if (!OnCurve(raw[1..33], raw[33..])) throw new CryptographicException("The key is not a point on P-256.");
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = raw[1..33].ToArray(), Y = raw[33..].ToArray() },
        };
        p.Validate();
        return p;
    }

    private static readonly BigInteger P = Big("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    private static readonly BigInteger B = Big("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");

    private static BigInteger Big(string hex) => BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);

    /// <summary>y² = x³ − 3x + b (mod p), with both coordinates below p.</summary>
    internal static bool OnCurve(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var bx = new BigInteger(x, isUnsigned: true, isBigEndian: true);
        var by = new BigInteger(y, isUnsigned: true, isBigEndian: true);
        if (bx >= P || by >= P) return false;
        var right = ((BigInteger.ModPow(bx, 3, P) - 3 * bx + B) % P + P) % P;
        return BigInteger.ModPow(by, 2, P) == right;
    }

    /// <summary>True when <paramref name="signature"/> is a valid ECDSA P-256 SHA-256 signature of <paramref name="data"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength) return false;
        try
        {
            using var key = ECDsa.Create(FromRawPublicKey(publicKey));
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The 32-byte shared secret (the X coordinate) of ECDH between <paramref name="own"/> and a raw public key.</summary>
    public static byte[] SharedSecret(ECDiffieHellman own, ReadOnlySpan<byte> otherPublicKey)
    {
        using var other = ECDiffieHellman.Create(FromRawPublicKey(otherPublicKey));
        return own.DeriveRawSecretAgreement(other.PublicKey);
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    internal static string Base32(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}

/// <summary>
/// This Cuebot's long-term identity: an ECDSA P-256 key. Its public key names the relay ID and the fingerprint mod
/// devices pin; it signs every handshake and the relay's connect challenge. Stored (PKCS#8) with the other secrets.
/// </summary>
public sealed class IdentityKey : IDisposable
{
    private readonly ECDsa _key;

    private IdentityKey(ECDsa key)
    {
        _key = key;
        PublicKey = PortalKeys.RawPublicKey(key.ExportParameters(false));
        RelayId = PortalKeys.RelayId(PublicKey);
        Fingerprint = PortalKeys.Fingerprint(PublicKey);
    }

    public byte[] PublicKey { get; }
    public string RelayId { get; }
    public string Fingerprint { get; }

    public static IdentityKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static IdentityKey FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.KeySize != 256) throw new CryptographicException("The identity key is not a P-256 key.");
            return new IdentityKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>For test vectors: a key from its private scalar and public point.</summary>
    internal static IdentityKey FromParameters(ECParameters p) => new(ECDsa.Create(p));

    public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

    /// <summary>ECDSA P-256 SHA-256 in IEEE P1363 form (64 bytes).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>Opens a message the relay sealed to this key (<see cref="PortalSeal"/>): the same P-256 key, used for ECDH.</summary>
    public byte[] OpenSealed(ReadOnlySpan<byte> sealedBytes)
    {
        using var ecdh = ECDiffieHellman.Create(_key.ExportParameters(true));
        return PortalSeal.Open(ecdh, sealedBytes);
    }

    public void Dispose() => _key.Dispose();
}
