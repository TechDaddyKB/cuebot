using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Portal.Crypto;

namespace Cuebot.Portal.Tests;

/// <summary>
/// The .NET half of the portal encryption against portal/test/vectors.json, which the TypeScript tests check too.
/// CUEBOT_WRITE_PORTAL_VECTORS=1 adds a .NET-made signature to the source file for the TypeScript side to verify.
/// </summary>
public sealed class PortalCryptoTests
{
    private static readonly JsonObject V = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")))!.AsObject();

    private static byte[] B(string name) => PortalKeys.FromBase64Url((string)V[name]!);

    private static ECParameters Params(JsonNode jwk) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D = PortalKeys.FromBase64Url((string)jwk["d"]!),
        Q = new ECPoint { X = PortalKeys.FromBase64Url((string)jwk["x"]!), Y = PortalKeys.FromBase64Url((string)jwk["y"]!) },
    };

    private static ECDiffieHellman Ecdh(string name) => ECDiffieHellman.Create(Params(V[name]!));

    private static IdentityKey Identity() => IdentityKey.FromParameters(Params(V["identity"]!));

    [Fact]
    public void Base32_matches_RFC_4648_in_lowercase()
    {
        string[][] cases = [["", ""], ["f", "my"], ["fo", "mzxq"], ["foo", "mzxw6"], ["foob", "mzxw6yq"], ["fooba", "mzxw6ytb"], ["foobar", "mzxw6ytboi"]];
        foreach (var c in cases) Assert.Equal(c[1], PortalKeys.Base32(Encoding.ASCII.GetBytes(c[0])));
    }

    [Fact]
    public void Identity_key_gives_the_vector_relay_ID_and_fingerprint()
    {
        using var id = Identity();
        Assert.Equal(B("identityPublic"), id.PublicKey);
        Assert.Equal((string)V["relayId"]!, id.RelayId);
        Assert.Equal((string)V["fingerprint"]!, id.Fingerprint);
        Assert.Matches("^[a-z2-7]{32}$", id.RelayId);
    }

    [Fact]
    public void Identity_key_survives_a_PKCS8_round_trip()
    {
        using var id = IdentityKey.Create();
        using var back = IdentityKey.FromPkcs8(id.ExportPkcs8());
        Assert.Equal(id.RelayId, back.RelayId);
        Assert.True(PortalKeys.Verify(id.PublicKey, [1, 2, 3], back.Sign([1, 2, 3])));
    }

    [Fact]
    public void Transcript_shared_secret_and_keys_match_the_vectors()
    {
        using var client = Ecdh("clientEphemeral");
        using var server = Ecdh("serverEphemeral");
        var clientPublic = PortalKeys.RawPublicKey(client.ExportParameters(false));
        var serverPublic = PortalKeys.RawPublicKey(server.ExportParameters(false));
        var transcript = PortalHandshake.Transcript(clientPublic, serverPublic, B("identityPublic"));
        Assert.Equal(B("transcript"), transcript);
        var shared = PortalKeys.SharedSecret(client, serverPublic);
        Assert.Equal(B("shared"), shared);
        Assert.Equal(shared, PortalKeys.SharedSecret(server, clientPublic));
        var (c2s, s2c) = PortalHandshake.DeriveKeys(shared, transcript);
        Assert.Equal(B("c2s"), c2s);
        Assert.Equal(B("s2c"), s2c);
        Assert.Equal(B("clientHello"), PortalHandshake.ClientHello(client));
    }

    [Fact]
    public void Verifies_the_TypeScript_signature()
    {
        Assert.True(PortalKeys.Verify(B("identityPublic"), B("transcript"), B("jsSignature")));
        var bad = B("jsSignature");
        bad[5] ^= 1;
        Assert.False(PortalKeys.Verify(B("identityPublic"), B("transcript"), bad));
    }

    [Fact]
    public void Accept_produces_a_hello_and_session_that_match_the_vectors()
    {
        using var id = Identity();
        using var server = Ecdh("serverEphemeral");
        var (hello, session) = PortalHandshake.Accept(id, B("clientHello"), server);
        Assert.Equal(PortalHandshake.ServerHelloLength, hello.Length);
        Assert.True(PortalKeys.Verify(id.PublicKey, B("transcript"), hello[^PortalKeys.SignatureLength..]));

        using (session)
        {
            foreach (var f in V["frames"]!.AsArray())
            {
                var frame = PortalKeys.FromBase64Url((string)f!["frame"]!);
                var text = (string)f["text"]!;
                if ((string)f["from"]! == "client") Assert.Equal(text, Encoding.UTF8.GetString(session.Decrypt(frame)));
                else Assert.Equal(frame, session.Encrypt(Encoding.UTF8.GetBytes(text)));
            }
        }
    }

    [Fact]
    public void Finish_accepts_the_TypeScript_signed_hello_and_refuses_another_identity()
    {
        using var client = Ecdh("clientEphemeral");
        using var server = Ecdh("serverEphemeral");
        byte[] Hello(byte[] identity, byte[] signature) =>
            [PortalHandshake.Version, .. PortalKeys.RawPublicKey(server.ExportParameters(false)), .. identity, .. signature];

        using var session = PortalHandshake.Finish(client, Hello(B("identityPublic"), B("jsSignature")), (string)V["fingerprint"]!);
        var first = V["frames"]!.AsArray()[0]!;
        Assert.Equal(PortalKeys.FromBase64Url((string)first["frame"]!), session.Encrypt(Encoding.UTF8.GetBytes((string)first["text"]!)));

        using var other = IdentityKey.Create();
        Assert.Throws<CryptographicException>(() => PortalHandshake.Finish(client, Hello(other.PublicKey, other.Sign(B("transcript"))), (string)V["fingerprint"]!));
        var badSignature = B("jsSignature");
        badSignature[0] ^= 0x80;
        Assert.Throws<CryptographicException>(() => PortalHandshake.Finish(client, Hello(B("identityPublic"), badSignature), (string)V["fingerprint"]!));
    }

    [Fact]
    public void Fresh_handshake_round_trips_and_breaks_on_reorder_or_tamper()
    {
        using var id = IdentityKey.Create();
        var (ephemeral, clientHello) = PortalHandshake.Start();
        using (ephemeral)
        {
            var (serverHello, server) = PortalHandshake.Accept(id, clientHello);
            using var client = PortalHandshake.Finish(ephemeral, serverHello, id.Fingerprint);
            using (server)
            {
                Assert.Equal("hi", Encoding.UTF8.GetString(server.Decrypt(client.Encrypt("hi"u8))));
                Assert.Equal("back", Encoding.UTF8.GetString(client.Decrypt(server.Encrypt("back"u8))));

                var one = client.Encrypt("one"u8);
                var two = client.Encrypt("two"u8);
                Assert.Throws<AuthenticationTagMismatchException>(() => server.Decrypt(two));
                Assert.ThrowsAny<CryptographicException>(() => server.Decrypt(one));
            }
        }
    }

    [Fact]
    public void Rejects_malformed_hellos_and_keys()
    {
        using var id = IdentityKey.Create();
        Assert.Throws<CryptographicException>(() => PortalHandshake.Accept(id, new byte[PortalHandshake.ClientHelloLength]));
        Assert.Throws<CryptographicException>(() => PortalHandshake.Accept(id, [1, 4, 5]));
        var offCurve = new byte[PortalKeys.PublicKeyLength];
        offCurve[0] = 4;
        offCurve[1] = 1;
        Assert.ThrowsAny<CryptographicException>(() => PortalHandshake.Accept(id, [1, .. offCurve]));
        Assert.False(PortalKeys.Verify(offCurve, [1], new byte[64]));
        Assert.Throws<CryptographicException>(() => PortalKeys.FromRawPublicKey(offCurve));
        Assert.True(PortalKeys.OnCurve(B("identityPublic")[1..33], B("identityPublic")[33..]));
    }

    [Fact]
    public void Seal_matches_the_vector_and_opens()
    {
        var seal = V["seal"]!;
        using var recipient = ECDiffieHellman.Create(Params(seal["recipient"]!));
        using var ephemeral = ECDiffieHellman.Create(Params(seal["ephemeral"]!));
        var recipientPublic = PortalKeys.RawPublicKey(recipient.ExportParameters(false));
        var plaintext = Encoding.UTF8.GetBytes((string)seal["plaintext"]!);
        var expected = PortalKeys.FromBase64Url((string)seal["sealed"]!);
        Assert.Equal(expected, PortalSeal.Seal(recipientPublic, plaintext, ephemeral));
        Assert.Equal(plaintext, PortalSeal.Open(recipient, expected));

        expected[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => PortalSeal.Open(recipient, expected));
        using var someoneElse = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.ThrowsAny<CryptographicException>(() => PortalSeal.Open(someoneElse, PortalSeal.Seal(recipientPublic, plaintext)));
    }

    [Fact]
    public void Writes_the_dotnet_signature_when_asked()
    {
        if (Environment.GetEnvironmentVariable("CUEBOT_WRITE_PORTAL_VECTORS") != "1") return;
        using var id = Identity();
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../portal/test/vectors.json"));
        var json = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        json["dotnetSignature"] = PortalKeys.Base64Url(id.Sign(B("transcript")));
        File.WriteAllText(source, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    [Fact]
    public void Vectors_carry_a_dotnet_signature_that_verifies()
    {
        var signature = (string?)V["dotnetSignature"];
        Assert.False(string.IsNullOrEmpty(signature), "Run the .NET tests once with CUEBOT_WRITE_PORTAL_VECTORS=1.");
        Assert.True(PortalKeys.Verify(B("identityPublic"), B("transcript"), PortalKeys.FromBase64Url(signature!)));
    }
}
