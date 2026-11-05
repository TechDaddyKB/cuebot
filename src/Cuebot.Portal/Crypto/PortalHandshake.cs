using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cuebot.Portal.Crypto;

/// <summary>
/// The end-to-end handshake between a mod device and Cuebot (docs/MOD_PORTAL_PLAN.md, section 3). The relay only
/// forwards these bytes.
/// <list type="number">
/// <item>Client hello: <c>0x01 || clientEphemeral(65)</c>.</item>
/// <item>Server hello: <c>0x01 || serverEphemeral(65) || identityKey(65) || signature(64)</c>, the signature by the
/// identity key over the transcript <c>"cuebot-portal-v1" || clientEphemeral || serverEphemeral || identityKey</c>.</item>
/// <item>The client checks the identity key against the fingerprint it pinned from the invite, and the signature.</item>
/// <item>Both derive per-direction AES-256-GCM keys: HKDF-SHA256 over the ECDH secret, salt SHA-256(transcript),
/// info "c2s" and "s2c".</item>
/// </list>
/// </summary>
public static class PortalHandshake
{
    public const byte Version = 1;
    public const string Label = "cuebot-portal-v1";

    public const int ClientHelloLength = 1 + PortalKeys.PublicKeyLength;
    public const int ServerHelloLength = 1 + 2 * PortalKeys.PublicKeyLength + PortalKeys.SignatureLength;

    /// <summary>Cuebot's side: answer a client hello. Throws <see cref="CryptographicException"/> for a bad hello.</summary>
    public static (byte[] ServerHello, PortalSession Session) Accept(IdentityKey identity, ReadOnlySpan<byte> clientHello)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Accept(identity, clientHello, ephemeral);
    }

    internal static (byte[] ServerHello, PortalSession Session) Accept(IdentityKey identity, ReadOnlySpan<byte> clientHello, ECDiffieHellman ephemeral)
    {
        if (clientHello.Length != ClientHelloLength || clientHello[0] != Version) throw new CryptographicException("Not a portal client hello.");
        var clientEphemeral = clientHello[1..];
        var serverEphemeral = PortalKeys.RawPublicKey(ephemeral.ExportParameters(false));
        var transcript = Transcript(clientEphemeral, serverEphemeral, identity.PublicKey);
        var signature = identity.Sign(transcript);

        var hello = new byte[ServerHelloLength];
        hello[0] = Version;
        serverEphemeral.CopyTo(hello, 1);
        identity.PublicKey.CopyTo(hello, 1 + PortalKeys.PublicKeyLength);
        signature.CopyTo(hello, 1 + 2 * PortalKeys.PublicKeyLength);

        var keys = DeriveKeys(PortalKeys.SharedSecret(ephemeral, clientEphemeral), transcript);
        return (hello, new PortalSession(send: keys.ServerToClient, receive: keys.ClientToServer));
    }

    /// <summary>A device's side: the ephemeral key and the hello to send.</summary>
    public static (ECDiffieHellman Ephemeral, byte[] ClientHello) Start()
    {
        var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (ephemeral, ClientHello(ephemeral));
    }

    internal static byte[] ClientHello(ECDiffieHellman ephemeral)
    {
        var hello = new byte[ClientHelloLength];
        hello[0] = Version;
        PortalKeys.RawPublicKey(ephemeral.ExportParameters(false)).CopyTo(hello, 1);
        return hello;
    }

    /// <summary>
    /// A device's side: check the server hello against the pinned <paramref name="fingerprint"/> and derive the session.
    /// Throws <see cref="CryptographicException"/> when the identity or signature does not match.
    /// </summary>
    public static PortalSession Finish(ECDiffieHellman ephemeral, ReadOnlySpan<byte> serverHello, string fingerprint)
    {
        if (serverHello.Length != ServerHelloLength || serverHello[0] != Version) throw new CryptographicException("Not a portal server hello.");
        var serverEphemeral = serverHello.Slice(1, PortalKeys.PublicKeyLength);
        var identity = serverHello.Slice(1 + PortalKeys.PublicKeyLength, PortalKeys.PublicKeyLength);
        var signature = serverHello[(1 + 2 * PortalKeys.PublicKeyLength)..];
        if (!CryptographicOperations.FixedTimeEquals(PortalKeys.Utf8(PortalKeys.Fingerprint(identity)), PortalKeys.Utf8(fingerprint)))
            throw new CryptographicException("This is not the Cuebot the invite was for.");

        var clientEphemeral = PortalKeys.RawPublicKey(ephemeral.ExportParameters(false));
        var transcript = Transcript(clientEphemeral, serverEphemeral, identity);
        if (!PortalKeys.Verify(identity, transcript, signature)) throw new CryptographicException("The handshake signature is wrong.");

        var keys = DeriveKeys(PortalKeys.SharedSecret(ephemeral, serverEphemeral), transcript);
        return new PortalSession(send: keys.ClientToServer, receive: keys.ServerToClient);
    }

    internal static byte[] Transcript(ReadOnlySpan<byte> clientEphemeral, ReadOnlySpan<byte> serverEphemeral, ReadOnlySpan<byte> identity) =>
        [.. PortalKeys.Utf8(Label), .. clientEphemeral, .. serverEphemeral, .. identity];

    internal static (byte[] ClientToServer, byte[] ServerToClient) DeriveKeys(byte[] shared, byte[] transcript)
    {
        var salt = SHA256.HashData(transcript);
        return (HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, PortalKeys.Utf8("c2s")),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, PortalKeys.Utf8("s2c")));
    }
}

/// <summary>
/// One encrypted connection: AES-256-GCM per direction, the nonce a frame counter (4 zero bytes || 8-byte big-endian
/// counter), a frame being ciphertext || 16-byte tag. The transport keeps frames in order, so a dropped, repeated or
/// reordered frame fails to decrypt and the connection must close.
/// </summary>
public sealed class PortalSession : IDisposable
{
    public const int TagLength = 16;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private ulong _sent;
    private ulong _received;
    private bool _broken;

    internal PortalSession(byte[] send, byte[] receive)
    {
        _send = new AesGcm(send, TagLength);
        _receive = new AesGcm(receive, TagLength);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var frame = new byte[plaintext.Length + TagLength];
        _send.Encrypt(Nonce(_sent++), plaintext, frame.AsSpan(0, plaintext.Length), frame.AsSpan(plaintext.Length));
        return frame;
    }

    /// <summary>Throws <see cref="CryptographicException"/> for a frame that is not the next one; the session is then unusable.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> frame)
    {
        if (_broken) throw new CryptographicException("The connection already failed.");
        if (frame.Length < TagLength) { _broken = true; throw new CryptographicException("The frame is too short."); }
        var plaintext = new byte[frame.Length - TagLength];
        try
        {
            _receive.Decrypt(Nonce(_received), frame[..plaintext.Length], frame[plaintext.Length..], plaintext);
        }
        catch (CryptographicException)
        {
            _broken = true;
            throw;
        }
        _received++;
        return plaintext;
    }

    internal static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}

/// <summary>
/// One-way sealed messages to a public key (the relay sealing a webhook to Cuebot): <c>0x01 || ephemeral(65) ||
/// ciphertext || tag</c>, the key HKDF-SHA256 over the ECDH secret with salt SHA-256("cuebot-seal-v1" || ephemeral ||
/// recipient) and info "seal", nonce zero (every message has a fresh ephemeral key).
/// </summary>
public static class PortalSeal
{
    public const string Label = "cuebot-seal-v1";

    public static byte[] Seal(ReadOnlySpan<byte> recipient, ReadOnlySpan<byte> plaintext)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Seal(recipient, plaintext, ephemeral);
    }

    internal static byte[] Seal(ReadOnlySpan<byte> recipient, ReadOnlySpan<byte> plaintext, ECDiffieHellman ephemeral)
    {
        var ephemeralPublic = PortalKeys.RawPublicKey(ephemeral.ExportParameters(false));
        var key = Key(PortalKeys.SharedSecret(ephemeral, recipient), ephemeralPublic, recipient);
        var sealedBytes = new byte[1 + PortalKeys.PublicKeyLength + plaintext.Length + PortalSession.TagLength];
        sealedBytes[0] = PortalHandshake.Version;
        ephemeralPublic.CopyTo(sealedBytes, 1);
        using var aes = new AesGcm(key, PortalSession.TagLength);
        var body = sealedBytes.AsSpan(1 + PortalKeys.PublicKeyLength);
        aes.Encrypt(new byte[12], plaintext, body[..plaintext.Length], body[plaintext.Length..]);
        return sealedBytes;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the message was not sealed to <paramref name="recipient"/> or was changed.</summary>
    public static byte[] Open(ECDiffieHellman recipient, ReadOnlySpan<byte> sealedBytes)
    {
        const int head = 1 + PortalKeys.PublicKeyLength;
        if (sealedBytes.Length < head + PortalSession.TagLength || sealedBytes[0] != PortalHandshake.Version)
            throw new CryptographicException("Not a sealed portal message.");
        var ephemeralPublic = sealedBytes[1..head];
        var recipientPublic = PortalKeys.RawPublicKey(recipient.ExportParameters(false));
        var key = Key(PortalKeys.SharedSecret(recipient, ephemeralPublic), ephemeralPublic, recipientPublic);
        var body = sealedBytes[head..];
        var plaintext = new byte[body.Length - PortalSession.TagLength];
        using var aes = new AesGcm(key, PortalSession.TagLength);
        aes.Decrypt(new byte[12], body[..plaintext.Length], body[plaintext.Length..], plaintext);
        return plaintext;
    }

    private static byte[] Key(byte[] shared, ReadOnlySpan<byte> ephemeral, ReadOnlySpan<byte> recipient) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32,
            SHA256.HashData([.. PortalKeys.Utf8(Label), .. ephemeral, .. recipient]), PortalKeys.Utf8("seal"));
}
